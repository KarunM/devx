using DevX.Domain;
using DevX.Indexer;
using Xunit;

namespace DevX.Tests.Indexer;

public sealed class ConfigAndTypeScriptReaderTests
{
    private const string Terraform = """
        terraform {
          required_version = ">= 1.6"
        }

        provider "aws" {
          region = "eu-west-1"
        }

        variable "env" {
          default = "dev" # a { in a comment
        }

        locals {
          name = "x-${var.env}"
          tags = { Env = var.env }
        }

        resource "aws_s3_bucket" "logs" {
          policy = <<EOT
        { "unbalanced":
        EOT
        }

        data "aws_caller_identity" "current" {}

        module "vpc" {
          source = "./vpc"
        }

        output "bucket" {
          value = aws_s3_bucket.logs.id
        }
        """;

    private static string Spans(ParsedFile file)
        => string.Join("|", file.Types.OrderBy(t => t.StartLine).Select(t => $"{t.Kind}:{t.Name}:{t.StartLine}-{t.EndLine}"));

    [Fact]
    public void Terraform_ExposesEveryBlockKindWithItsSpan()
    {
        var file = new CompositeLanguageParser().Parse("infra/bootstrap/main.tf", Terraform);
        Assert.Equal(
            "Terraform:terraform:1-3|Provider:provider.aws:5-7|Variable:variable.env:9-11|Locals:locals:13-16|Resource:aws_s3_bucket.logs:18-22|Data:data.aws_caller_identity.current:24-24|Module:module.vpc:26-28|Output:output.bucket:30-32",
            Spans(file));
        Assert.Equal("infra.bootstrap.main", file.Namespace);
        Assert.Equal(["name", "tags"], file.Types.Single(t => t.Kind == "Locals").Fields.Select(f => f.Name).ToArray());
    }

    [Fact]
    public void TerraformTest_RunBlocksAreSymbols()
    {
        var file = new CompositeLanguageParser().Parse("tests/main.tftest.hcl", "run \"creates_bucket\" {\n  command = plan\n  assert {\n    condition = true\n  }\n}\n\nrun \"second\" {\n}\n");
        Assert.Equal("Run:run.creates_bucket:1-6|Run:run.second:8-9", Spans(file));
    }

    [Fact]
    public void Tfvars_AndTfbackend_AreParsedAndAssignmentsBecomeFields()
    {
        var vars = new CompositeLanguageParser().Parse("prod.tfvars", "env = \"prod\"\nregions = [\n  \"a\",\n]\n");
        Assert.Equal(["env:1-1", "regions:2-4"], vars.Types.Single().Fields.Select(f => $"{f.Name}:{f.StartLine}-{f.EndLine}").ToArray());
        Assert.True(new CompositeLanguageParser().CanParse("backend.tfbackend"));
        Assert.True(new CompositeLanguageParser().CanParse("a.tftest.hcl"));
    }

    [Theory]
    [InlineData("resource \"a\" {\n  x = {\n")]
    [InlineData("}}} \"\" resource")]
    [InlineData("")]
    public void Terraform_MalformedInputYieldsNoSymbolsAndDoesNotThrow(string text)
    {
        var file = new CompositeLanguageParser().Parse("bad.tf", text);
        Assert.Empty(file.Types);
        Assert.Equal("bad", file.Namespace);
    }

    private const string Workflow = """
        name: CI
        on:
          push:
            branches: [main]
        jobs:
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@v4
              - name: Run tests
                run: dotnet test
              - id: pack
                run: dotnet pack
          deploy:
            steps:
              - run: echo deploy
        """;

    [Fact]
    public void WorkflowYaml_TopLevelKeysJobsAndSteps()
    {
        var file = new CompositeLanguageParser().Parse(".github/workflows/ci.yml", Workflow);
        Assert.Equal("Key:name:1-1|Key:on:2-4|Key:jobs:5-16", Spans(file));
        Assert.Equal("github.workflows.ci", file.Namespace);
        var jobs = file.Types.Single(t => t.Name == "jobs");
        Assert.Equal(
            ["build:6-13", "build.actions/checkout@v4:9-9", "build.Run tests:10-11", "build.pack:12-13", "deploy:14-16", "deploy.echo deploy:16-16"],
            jobs.Methods.Select(m => $"{m.Name}:{m.StartLine}-{m.EndLine}").ToArray());
    }

    [Fact]
    public void PlainYaml_KeysAndChildren_BlockScalarContentIsNotAKey()
    {
        var file = new CompositeLanguageParser().Parse("config.yaml", "services:\n  web:\n    image: x\n  db:\n    image: y\nnotes: |\n  fake: key\n  other: line\n");
        Assert.Equal("Key:services:1-5|Key:notes:6-8", Spans(file));
        Assert.Equal(["web", "db"], file.Types[0].Methods.Select(m => m.Name).ToArray());
        Assert.Empty(file.Types[1].Methods);
    }

    [Theory]
    [InlineData("jobs:\n  a: [\n  : : :\n- - x\n")]
    [InlineData("\t\t:::\n---\n- - -\n")]
    [InlineData("")]
    public void Yaml_MalformedInputDoesNotThrow(string text)
        => Assert.NotNull(new CompositeLanguageParser().Parse("bad.yml", text));

    [Fact]
    public void Toml_TablesAndArrayTablesWithRootKeys()
    {
        var file = new CompositeLanguageParser().Parse("pyproject.toml", "title = \"x\"\n\n[project]\nname = \"demo\"\ndoc = \"\"\"\n[not.a.table]\n\"\"\"\n\n[[tool.items]]\nx = 1\n");
        Assert.Equal("Table:(root):1-1|Table:project:3-7|ArrayTable:tool.items:9-10", Spans(file));
        Assert.Equal(["name", "doc"], file.Types.Single(t => t.Name == "project").Fields.Select(f => f.Name).ToArray());
    }

    [Theory]
    [InlineData("[[[ nope\n= = =\n")]
    [InlineData("[unterminated\nkey = \"\"\"never closed\n")]
    public void Toml_MalformedInputDoesNotThrow(string text)
        => Assert.NotNull(new CompositeLanguageParser().Parse("bad.toml", text));

    private const string TypeScript = """
        export interface User { id: number; name: string }
        export type Id = string | number;
        export enum Color { Red, Green }
        export class Svc<T> {
          private readonly items: T[] = [];
          public async add(item: T): Promise<void> {
            this.items.push(item);
          }
        }
        export function greet<T>(u: User): string {
          return `hi ${u.name}`;
        }
        export const double = (n: number): number => {
          return n * 2;
        };
        const handlers: Record<string, () => void> = {
          a: () => {},
        };
        """;

    [Theory]
    [InlineData("src/app.ts")]
    [InlineData("src/app.tsx")]
    public void TypeScript_FunctionsClassesConstsAndTypeDeclarations(string path)
    {
        var file = new CompositeLanguageParser().Parse(path, TypeScript);
        Assert.Equal("Interface:User:1-1|TypeAlias:Id:2-2|Enum:Color:3-3|Class:Svc:4-9", Spans(file with { Types = file.Types.Where(t => t.Kind != "Module").ToList() }));
        var module = file.Types.Single(t => t.Kind == "Module");
        Assert.Equal(["greet:10-12", "double:13-15"], module.Methods.Select(m => $"{m.Name}:{m.StartLine}-{m.EndLine}").ToArray());
        Assert.Equal(["handlers:16-18"], module.Fields.Select(f => $"{f.Name}:{f.StartLine}-{f.EndLine}").ToArray());
        Assert.Equal(["add:6-8"], file.Types.Single(t => t.Name == "Svc").Methods.Select(m => $"{m.Name}:{m.StartLine}-{m.EndLine}").ToArray());
    }

    [Fact]
    public void TypeScript_MalformedFileDoesNotThrow()
        => Assert.NotNull(new CompositeLanguageParser().Parse("bad.ts", "function broken( {\n  const x = [\n interface {\n type = \n"));

    [Fact]
    public void StructuredSymbolReader_LabelsTheNewLanguages()
    {
        Assert.Equal("Terraform", StructuredSymbolReader.LanguageOf("a.tf"));
        Assert.Equal("YAML", StructuredSymbolReader.LanguageOf("a.yml"));
        Assert.Equal("TOML", StructuredSymbolReader.LanguageOf("a.toml"));
        Assert.Equal("TypeScript", StructuredSymbolReader.LanguageOf("a.tsx"));
        Assert.Equal("JavaScript", StructuredSymbolReader.LanguageOf("a.mjs"));
    }
}
