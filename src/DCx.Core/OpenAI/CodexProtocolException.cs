namespace DCx.Core.OpenAI;

public sealed class CodexProtocolException : Exception
{
    public CodexProtocolException(string message) : base(message)
    {
    }

    public CodexProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
