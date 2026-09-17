namespace WriteFix.Models;

/// <summary>Which kind of service answers correction requests.</summary>
public enum AiProvider
{
    /// <summary>An OpenAI-compatible API at <see cref="AppSettings.ApiBaseUrl"/> (Groq, OpenRouter…), with the user's key.</summary>
    ChatCompletions,

    /// <summary>A local OpenCode server started by WriteFix, using the providers connected in OpenCode.</summary>
    OpenCode,
}
