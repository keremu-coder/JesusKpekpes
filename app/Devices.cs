using NAudio.CoreAudioApi;

namespace JesusKpekpes;

sealed record AudioDevice(string Id, string Name, int Channels, int Rate, double PeriodMs)
{
    public override string ToString() => $"{Name}   ·   {Channels} speakers";
}

static class Devices
{
    public static List<AudioDevice> Render()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            var format = device.AudioClient.MixFormat;
            var period = device.AudioClient.DefaultDevicePeriod / 10_000d;
            list.Add(new AudioDevice(device.ID, device.FriendlyName, format.Channels, format.SampleRate, period));
            device.Dispose();
        }
        return list;
    }

    public static string? DefaultId()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        return device.ID;
    }
}
