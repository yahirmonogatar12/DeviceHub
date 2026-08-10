using DeviceHub.Contracts;

namespace DeviceHub.Server.Data;

/// <summary>Traduccion entre los ENUM de MySQL y los del contrato gRPC.</summary>
public static class Map
{
    public static string ToDb(FingerprintConfidence confidence) => confidence switch
    {
        FingerprintConfidence.High => "high",
        FingerprintConfidence.Medium => "medium",
        _ => "low"
    };

    public static FingerprintConfidence Confidence(string? value) => value switch
    {
        "high" => FingerprintConfidence.High,
        "medium" => FingerprintConfidence.Medium,
        _ => FingerprintConfidence.Low
    };

    public static IdentityState Identity(string? value)
        => value == "identity_conflict" ? IdentityState.Conflict : IdentityState.Ok;
}
