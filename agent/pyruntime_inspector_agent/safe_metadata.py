MAX_METADATA_TEXT_LENGTH = 256


def bounded_text(value, fallback, maximum=MAX_METADATA_TEXT_LENGTH):
    """Return a bounded exact string without coercing target-owned values."""
    if type(value) is not str:
        return fallback
    if len(value) <= maximum:
        return value
    return value[:maximum] + "…"


def type_text(cls, attribute, fallback):
    """Read type metadata without formatting or coercing target-owned values."""
    try:
        value = type.__getattribute__(cls, attribute)
    except AttributeError:
        return fallback
    return bounded_text(value, fallback)


def type_name(cls):
    return type_text(cls, "__name__", "<unnamed>")


def type_qualified_name(cls):
    return type_text(cls, "__qualname__", type_name(cls))


def type_module(cls):
    return type_text(cls, "__module__", "<unknown>")


def is_class_object(value):
    """Return whether value is a class without consulting value.__class__."""
    try:
        metaclass_mro = type.__getattribute__(type(value), "__mro__")
    except (AttributeError, TypeError):
        return False
    return any(base is type for base in metaclass_mro)


def is_dict_object(value):
    """Return whether value is a dict or subclass without reading value.__class__."""
    try:
        value_type_mro = type.__getattribute__(type(value), "__mro__")
    except (AttributeError, TypeError):
        return False
    return any(base is dict for base in value_type_mro)


def exact_dict_value(mapping, name, default=None):
    """Read an exact string key without comparing target-owned keys."""
    if not is_dict_object(mapping) or type(name) is not str:
        return default
    try:
        for key, value in dict.items(mapping):
            if type(key) is str and str.__eq__(key, name):
                return value
    except RuntimeError:
        return default
    return default


def exact_dict_string_values(mapping, names):
    """Read several exact string keys in one callback-free dictionary pass."""
    if not is_dict_object(mapping):
        return {}
    wanted = frozenset(names)
    values = {}
    try:
        for key, value in dict.items(mapping):
            if type(key) is str and key in wanted:
                values[key] = value
    except RuntimeError:
        return {}
    return values
