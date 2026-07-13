import sys
import types

from .safe_metadata import is_class_object, type_module, type_name, type_qualified_name


MAX_CLASS_MEMBERS = 200
MAX_CLASS_MEMBER_SCAN = 5_000
MAX_PARAMETERS = 256
_MAX_NAME_LENGTH = 256
_MAX_SIGNATURE_LENGTH = 2_048
_MAX_ANNOTATION_LENGTH = 160
_MAX_DEFAULT_LENGTH = 160
_MAX_SOURCE_LENGTH = 1_024
_MAX_MRO_ENTRIES = 128
_MAX_DIRECT_INT_BITS = 4096
_MISSING = object()


def _clip(value, limit):
    return value if len(value) <= limit else value[:limit - 1] + "…"


def _class_name(cls):
    return _clip(type_qualified_name(cls), 500)


def _class_ref(cls):
    module = type_module(cls)
    qualified_name = type_qualified_name(cls)
    return {
        "name": _clip(type_name(cls), _MAX_NAME_LENGTH),
        "qualifiedName": _clip(qualified_name, 500),
        "module": _clip(module, 500),
        "displayName": _clip(f"{module}.{qualified_name}", 1_000),
        "addressHex": hex(id(cls)),
    }


def _signature(callable_value):
    details = _signature_details(callable_value)
    return details["display"] if details is not None else None


def _signature_details(callable_value):
    if type(callable_value) is not types.FunctionType:
        return None

    code = types.FunctionType.__getattribute__(callable_value, "__code__")
    defaults = types.FunctionType.__getattribute__(callable_value, "__defaults__")
    keyword_defaults = types.FunctionType.__getattribute__(callable_value, "__kwdefaults__")
    defaults = defaults if type(defaults) is tuple else ()
    keyword_defaults = keyword_defaults if type(keyword_defaults) is dict else {}
    annotations = _materialized_annotations(callable_value)

    positional_count = code.co_argcount
    positional_only_count = code.co_posonlyargcount
    keyword_only_count = code.co_kwonlyargcount
    names = code.co_varnames
    records = []
    display_parts = []
    truncated = False

    first_default = positional_count - len(defaults)
    for index in range(positional_count):
        default = defaults[index - first_default] if index >= first_default else _MISSING
        record, text = _parameter(names[index], "positionalOnly" if index < positional_only_count else "positionalOrKeyword", default, annotations)
        truncated = _append_parameter(records, display_parts, record, text) or truncated
        if positional_only_count and index + 1 == positional_only_count:
            display_parts.append("/")

    keyword_start = positional_count
    variable_index = positional_count + keyword_only_count
    has_varargs = bool(code.co_flags & 0x04)
    has_varkw = bool(code.co_flags & 0x08)
    if has_varargs:
        record, text = _parameter(names[variable_index], "varPositional", _MISSING, annotations, "*")
        truncated = _append_parameter(records, display_parts, record, text) or truncated
        variable_index += 1
    elif keyword_only_count:
        display_parts.append("*")

    for index in range(keyword_only_count):
        name = names[keyword_start + index]
        default = dict.get(keyword_defaults, name, _MISSING)
        record, text = _parameter(name, "keywordOnly", default, annotations)
        truncated = _append_parameter(records, display_parts, record, text) or truncated

    if has_varkw:
        record, text = _parameter(names[variable_index], "varKeyword", _MISSING, annotations, "**")
        truncated = _append_parameter(records, display_parts, record, text) or truncated

    if truncated:
        display_parts.append("…")
    return_annotation = dict.get(annotations, "return", _MISSING)
    return_annotation_text = None if return_annotation is _MISSING else _safe_annotation(return_annotation)
    display = "(" + ", ".join(display_parts) + ")"
    if return_annotation_text is not None:
        display += " -> " + return_annotation_text
    if len(display) > _MAX_SIGNATURE_LENGTH:
        display = _clip(display, _MAX_SIGNATURE_LENGTH)
        truncated = True
    return {
        "display": display,
        "parameters": records,
        "returnAnnotation": return_annotation_text,
        "annotationsOmittedForSafety": sys.version_info >= (3, 14),
        "truncated": truncated,
    }


def _materialized_annotations(function):
    # Python 3.14 can evaluate deferred annotations when __annotations__ is read.
    # Omitting them is safer than executing the target's annotation function.
    if sys.version_info >= (3, 14):
        return {}
    annotations = types.FunctionType.__getattribute__(function, "__annotations__")
    return annotations if type(annotations) is dict else {}


def _append_parameter(records, display_parts, record, text):
    if len(records) >= MAX_PARAMETERS:
        return True
    records.append(record)
    display_parts.append(text)
    return False


def _parameter(name, kind, default, annotations, prefix=""):
    clipped_name = _clip(name, _MAX_NAME_LENGTH)
    annotation = dict.get(annotations, name, _MISSING)
    annotation_text = None if annotation is _MISSING else _safe_annotation(annotation)
    default_text = None if default is _MISSING else _safe_default(default)
    text = prefix + clipped_name
    if annotation_text is not None:
        text += ": " + annotation_text
    if default_text is not None:
        text += "=" + default_text
    return {
        "name": clipped_name,
        "kind": kind,
        "hasDefault": default is not _MISSING,
        "defaultPreview": default_text,
        "hasAnnotation": annotation is not _MISSING,
        "annotationText": annotation_text,
    }, text


def _safe_annotation(annotation):
    if type(annotation) is str:
        return _clip(annotation, _MAX_ANNOTATION_LENGTH)
    if is_class_object(annotation):
        module = type_module(annotation)
        name = type_qualified_name(annotation)
        text = name if module == "builtins" else f"{module}.{name}"
        return _clip(text, _MAX_ANNOTATION_LENGTH)
    return "<annotation>"


def _safe_default(value):
    if value is None:
        return "None"
    exact = type(value)
    if exact is bool:
        return "True" if value else "False"
    if exact is int:
        bit_length = int.bit_length(value)
        return repr(value) if bit_length <= _MAX_DIRECT_INT_BITS else f"<int bits={bit_length}>"
    if exact in (float, complex):
        return repr(value)
    if exact is str:
        clipped = value[:_MAX_DEFAULT_LENGTH]
        return _clip(repr(clipped), _MAX_DEFAULT_LENGTH)
    if exact is bytes:
        clipped = value[:_MAX_DEFAULT_LENGTH // 2]
        return _clip(repr(clipped), _MAX_DEFAULT_LENGTH)
    return "<default>"


def _classification(raw):
    if type(raw) is staticmethod:
        wrapped = object.__getattribute__(raw, "__func__")
        return "staticmethod", wrapped if type(wrapped) is types.FunctionType else None
    if type(raw) is classmethod:
        wrapped = object.__getattribute__(raw, "__func__")
        return "classmethod", wrapped if type(wrapped) is types.FunctionType else None
    if type(raw) is property:
        return "property", None
    if type(raw) is types.FunctionType:
        return "instance method", raw
    if type(raw) is types.BuiltinFunctionType:
        return "function", None
    if type(raw) is types.MethodDescriptorType:
        return "method descriptor", None
    descriptor_methods = set()
    for base in type.__getattribute__(type(raw), "__mro__"):
        namespace = type.__getattribute__(base, "__dict__")
        descriptor_methods.update(name for name in ("__get__", "__set__", "__delete__") if name in namespace)
    if "__get__" in descriptor_methods and ("__set__" in descriptor_methods or "__delete__" in descriptor_methods):
        return "data descriptor", None
    if is_class_object(raw):
        return "nested class", None
    if "__get__" in descriptor_methods:
        return "unknown descriptor", None
    return "class attribute", None


def _group(kind):
    if kind in ("instance method", "staticmethod", "classmethod", "function", "method descriptor"):
        return "methods"
    if kind in ("property", "data descriptor", "unknown descriptor"):
        return "propertiesAndDescriptors"
    return "classAttributes"


def _source(function):
    if type(function) is not types.FunctionType:
        return None
    code = types.FunctionType.__getattribute__(function, "__code__")
    return {
        "file": _clip(code.co_filename, _MAX_SOURCE_LENGTH),
        "line": int(code.co_firstlineno),
    }


def describe(value):
    cls = value if is_class_object(value) else type(value)
    complete_mro = type.__getattribute__(cls, "__mro__")
    mro = complete_mro[:_MAX_MRO_ENTRIES]
    members = []
    seen = set()
    scanned = 0
    effective_total = 0
    scan_truncated = False
    for base in mro:
        namespace = type.__getattribute__(base, "__dict__")
        for name, raw in namespace.items():
            scanned += 1
            if scanned > MAX_CLASS_MEMBER_SCAN:
                scan_truncated = True
                break
            if name in seen:
                continue
            seen.add(name)
            effective_total += 1
            if len(members) >= MAX_CLASS_MEMBERS:
                continue
            kind, signature_target = _classification(raw)
            signature_details = _signature_details(signature_target) if signature_target is not None else None
            declared_by = _class_ref(base)
            members.append({
                "name": _clip(name, _MAX_NAME_LENGTH),
                "kind": kind,
                "group": _group(kind),
                "declaredBy": declared_by["qualifiedName"],
                "declaredByRef": declared_by,
                "inherited": base is not cls,
                "signature": signature_details["display"] if signature_details is not None else None,
                "signatureDetails": signature_details,
                "parameters": signature_details["parameters"] if signature_details is not None else [],
                "source": _source(signature_target),
            })
        if scan_truncated:
            break
    namespace = type.__getattribute__(cls, "__dict__")
    doc = namespace.get("__doc__")
    if type(doc) is str:
        doc = _clip(doc, 500)
    else:
        doc = None
    bases = type.__getattribute__(cls, "__bases__")
    return {
        "name": _clip(type_name(cls), _MAX_NAME_LENGTH),
        "qualifiedName": _class_name(cls),
        "module": _clip(type_module(cls), 500),
        "addressHex": hex(id(cls)),
        "baseClasses": [_class_name(base) for base in bases[:_MAX_MRO_ENTRIES]],
        "baseClassRefs": [_class_ref(base) for base in bases[:_MAX_MRO_ENTRIES]],
        "mro": [_class_name(base) for base in mro],
        "mroRefs": [_class_ref(base) for base in mro],
        "mroTruncated": len(complete_mro) > len(mro),
        "metaclass": _class_name(type(cls)),
        "metaclassRef": _class_ref(type(cls)),
        "docstring": doc,
        "members": members,
        "memberTotal": effective_total if not scan_truncated else None,
        "membersScanned": scanned,
        "membersTruncated": scan_truncated or effective_total > len(members),
        "memberLimit": MAX_CLASS_MEMBERS,
    }
