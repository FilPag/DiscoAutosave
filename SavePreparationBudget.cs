namespace DiscoAutosave;

internal static class SavePreparationBudget
{
    internal static double Milliseconds(double frameSeconds) => double.IsFinite(frameSeconds)
        ? Math.Clamp(frameSeconds * 100.0, 0.25, 4.0)
        : 0.25;
}
