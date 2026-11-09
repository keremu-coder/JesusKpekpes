namespace JesusKpekpes;

public static class Suppress
{
    public static void Apply(float[] left, float[] right, int sampleRate) => TuneEngine.Run(left, right, sampleRate);
}
