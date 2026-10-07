import ast
import json
import sys


def visibility(name):
    return "private" if name.startswith("_") else "public"


def end_line(node, source_lines):
    end = getattr(node, "end_lineno", None)
    if end is not None:
        return end
    # Fallback for very old Pythons without end_lineno: best-effort to node's own start.
    return node.lineno


def build_method(node, source_lines):
    is_ctor = node.name == "__init__"
    try:
        signature = ast.unparse(node.args)
    except Exception:
        signature = ""
    return {
        "Name": node.name,
        "Signature": f"{node.name}({signature})",
        "ReturnType": "",
        "Visibility": visibility(node.name),
        "StartLine": node.lineno,
        "EndLine": end_line(node, source_lines),
        "IsConstructor": is_ctor,
    }


def build_class(node, source_lines):
    methods = []
    fields = []
    for item in node.body:
        if isinstance(item, (ast.FunctionDef, ast.AsyncFunctionDef)):
            methods.append(build_method(item, source_lines))
        elif isinstance(item, ast.Assign):
            for target in item.targets:
                if isinstance(target, ast.Name):
                    fields.append({
                        "Name": target.id,
                        "Type": "",
                        "Visibility": visibility(target.id),
                        "StartLine": item.lineno,
                        "EndLine": end_line(item, source_lines),
                    })
        elif isinstance(item, ast.AnnAssign) and isinstance(item.target, ast.Name):
            fields.append({
                "Name": item.target.id,
                "Type": ast.unparse(item.annotation) if item.annotation is not None else "",
                "Visibility": visibility(item.target.id),
                "StartLine": item.lineno,
                "EndLine": end_line(item, source_lines),
            })
    return {
        "Name": node.name,
        "Kind": "Class",
        "Namespace": None,
        "Visibility": visibility(node.name),
        "StartLine": node.lineno,
        "EndLine": end_line(node, source_lines),
        "Methods": methods,
        "Properties": [],
        "Fields": fields,
    }


def build_module_field(item, source_lines, name):
    return {
        "Name": name,
        "Type": "",
        "Visibility": visibility(name),
        "StartLine": item.lineno,
        "EndLine": end_line(item, source_lines),
    }


def build_module_ann_field(item, source_lines):
    return {
        "Name": item.target.id,
        "Type": ast.unparse(item.annotation) if item.annotation is not None else "",
        "Visibility": visibility(item.target.id),
        "StartLine": item.lineno,
        "EndLine": end_line(item, source_lines),
    }


def main():
    file_path = sys.argv[1]
    with open(file_path, "r", encoding="utf-8", errors="replace") as f:
        source = f.read()
    source_lines = source.splitlines()

    try:
        tree = ast.parse(source, filename=file_path)
    except SyntaxError as exc:
        print(json.dumps({"FilePath": file_path, "Namespace": None, "Types": [], "Error": str(exc)}))
        return

    types = []
    module_functions = []
    module_fields = []

    for node in tree.body:
        if isinstance(node, ast.ClassDef):
            types.append(build_class(node, source_lines))
        elif isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef)):
            module_functions.append(build_method(node, source_lines))
        elif isinstance(node, ast.Assign):
            for target in node.targets:
                if isinstance(target, ast.Name):
                    module_fields.append(build_module_field(node, source_lines, target.id))
        elif isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name):
            module_fields.append(build_module_ann_field(node, source_lines))

    # Module-level functions and assignments have no enclosing class; represent the module
    # itself as a synthetic "Module" type so they still get StartLine/EndLine + a container,
    # matching the shape RepositoryIndexingService expects (namespace + type + methods/fields).
    # This is also what makes constants like VOICE_ID or VOICE_DIR reachable through
    # `devx search symbol`, which otherwise only ever saw classes/functions/methods.
    if module_functions or module_fields:
        types.append({
            "Name": "<module>",
            "Kind": "Module",
            "Namespace": None,
            "Visibility": "public",
            "StartLine": 1,
            "EndLine": len(source_lines) if source_lines else 1,
            "Methods": module_functions,
            "Properties": [],
            "Fields": module_fields,
        })

    result = {
        "FilePath": file_path,
        "Namespace": None,
        "Types": types,
    }
    print(json.dumps(result))


if __name__ == "__main__":
    main()
