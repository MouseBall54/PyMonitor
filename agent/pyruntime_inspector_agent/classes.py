import inspect
import types


def _class_name(cls):
    return type.__getattribute__(cls, "__qualname__")


def _signature(callable_value):
    try:
        signature = inspect.signature(callable_value, follow_wrapped=False, eval_str=False)
    except (TypeError, ValueError):
        return None
    parts = []
    parameters = list(signature.parameters.values())
    inserted_keyword_separator = False
    for index, parameter in enumerate(parameters):
        if parameter.kind is inspect.Parameter.KEYWORD_ONLY and not inserted_keyword_separator:
            if not any(prior.kind is inspect.Parameter.VAR_POSITIONAL for prior in parameters[:index]):
                parts.append("*")
            inserted_keyword_separator = True
        prefix = "*" if parameter.kind is inspect.Parameter.VAR_POSITIONAL else "**" if parameter.kind is inspect.Parameter.VAR_KEYWORD else ""
        text = prefix + parameter.name
        if parameter.annotation is not inspect.Parameter.empty:
            text += ": " + _safe_annotation(parameter.annotation)
        if parameter.default is not inspect.Parameter.empty:
            text += "=" + _safe_default(parameter.default)
        parts.append(text)
        if parameter.kind is inspect.Parameter.POSITIONAL_ONLY and (index + 1 == len(parameters) or parameters[index + 1].kind is not inspect.Parameter.POSITIONAL_ONLY):
            parts.append("/")
    result = "(" + ", ".join(parts) + ")"
    if signature.return_annotation is not inspect.Signature.empty:
        result += " -> " + _safe_annotation(signature.return_annotation)
    return result


def _safe_annotation(annotation):
    if type(annotation) is str:
        return annotation[:120]
    if isinstance(annotation, type):
        module = type.__getattribute__(annotation, "__module__")
        name = type.__getattribute__(annotation, "__qualname__")
        return name if module == "builtins" else f"{module}.{name}"
    return "<annotation>"


def _safe_default(value):
    if value is None:
        return "None"
    if type(value) in (bool, int, float, complex, str, bytes):
        return repr(value)
    return "<default>"


def _classification(raw):
    if type(raw) is staticmethod:
        return "staticmethod", raw.__func__
    if type(raw) is classmethod:
        return "classmethod", raw.__func__
    if type(raw) is property:
        return "property", None
    if type(raw) is types.FunctionType:
        return "instance method", raw
    if isinstance(raw, types.MethodDescriptorType):
        return "method descriptor", raw
    descriptor_methods = set()
    for base in type.__getattribute__(type(raw), "__mro__"):
        namespace = type.__getattribute__(base, "__dict__")
        descriptor_methods.update(name for name in ("__get__", "__set__", "__delete__") if name in namespace)
    if "__get__" in descriptor_methods and ("__set__" in descriptor_methods or "__delete__" in descriptor_methods):
        return "data descriptor", None
    if isinstance(raw, type):
        return "nested class", None
    if "__get__" in descriptor_methods:
        return "unknown descriptor", None
    return "class attribute", None


def describe(value):
    cls = value if isinstance(value, type) else type(value)
    mro = type.__getattribute__(cls, "__mro__")
    members = []
    seen = set()
    for base in mro:
        namespace = type.__getattribute__(base, "__dict__")
        for name, raw in namespace.items():
            if name in seen:
                continue
            seen.add(name)
            kind, signature_target = _classification(raw)
            members.append({
                "name": name,
                "kind": kind,
                "declaredBy": _class_name(base),
                "signature": _signature(signature_target) if signature_target is not None else None,
            })
    namespace = type.__getattribute__(cls, "__dict__")
    doc = namespace.get("__doc__")
    if type(doc) is str:
        doc = doc[:500]
    else:
        doc = None
    return {
        "name": type.__getattribute__(cls, "__name__"),
        "qualifiedName": _class_name(cls),
        "module": type.__getattribute__(cls, "__module__"),
        "addressHex": hex(id(cls)),
        "baseClasses": [_class_name(base) for base in type.__getattribute__(cls, "__bases__")],
        "mro": [_class_name(base) for base in mro],
        "metaclass": _class_name(type(cls)),
        "docstring": doc,
        "members": members,
    }
