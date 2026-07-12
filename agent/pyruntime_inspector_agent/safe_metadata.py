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
