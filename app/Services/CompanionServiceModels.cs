namespace AzerothCompanion.Services;

public enum ReasoningMode { LocalCpu, HybridOpenAI }

public sealed record ReasoningRequest(
    string UserText,
    string ScreenText = "",
    string PersonaInstruction = "",
    string GameContext = "",
    ReasoningMode Mode = ReasoningMode.LocalCpu,
    byte[]? ImagePngOrJpeg = null,
    int MaxOutputTokens = 400);

public sealed record ReasoningResult(string Text, string Provider, decimal ChargedUsd, BudgetSnapshot Budget);
public sealed record BudgetSnapshot(string Month, decimal LimitUsd, decimal ChargedUsd,
    decimal ReservedUsd, decimal RemainingUsd, int UncertainRequests, bool CloudBlocked = false, bool IsAvailable = true);
public sealed record BudgetReservation(string Id, string Month, decimal ReservedUsd);
public sealed record NarrationStatus(bool Ready, string State, string? NarratorPhase, string? CurrentVoice);
public sealed record NarrationAudio(byte[] PcmFloat32, int SampleRate, int Channels, string ProfileId,
    string RequestId, double? RenderSeconds = null)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)PcmFloat32.Length / (sizeof(float) * Channels * SampleRate));
}

public sealed class CompanionServiceException(string message) : Exception(message);
public sealed class BudgetException(string message) : Exception(message);
