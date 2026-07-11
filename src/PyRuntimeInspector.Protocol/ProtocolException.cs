namespace PyRuntimeInspector.Protocol;

public class ProtocolException(string message) : Exception(message);

public sealed class RemoteInspectionException(string code, string message) : ProtocolException(message)
{
    public string Code { get; } = code;
}
