using System.Runtime.InteropServices;
using System.Numerics;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Storage;

namespace Beans.Windows;

internal sealed class LocalSpectrumAnalyzer : IAsyncDisposable
{
    private AudioGraph? graph;
    private AudioFileInputNode? input;
    private AudioFrameOutputNode? output;

    public event EventHandler<double[]>? SpectrumAvailable;

    public async Task StartAsync(string path)
    {
        await ResetAsync();
        var settings = new AudioGraphSettings(global::Windows.Media.Render.AudioRenderCategory.Media)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.ClosestToDesired,
            DesiredSamplesPerQuantum = 1024
        };
        var graphResult = await AudioGraph.CreateAsync(settings);
        if (graphResult.Status != AudioGraphCreationStatus.Success) return;
        graph = graphResult.Graph;
        output = graph.CreateFrameOutputNode();
        var file = await StorageFile.GetFileFromPathAsync(path);
        var inputResult = await graph.CreateFileInputNodeAsync(file);
        if (inputResult.Status != AudioFileNodeCreationStatus.Success)
        {
            await ResetAsync();
            return;
        }
        input = inputResult.FileInputNode;
        input.AddOutgoingConnection(output);
        graph.QuantumProcessed += Graph_QuantumProcessed;
        graph.Start();
        input.Start();
    }

    public void Pause() => input?.Stop();
    public void Resume() => input?.Start();

    private unsafe void Graph_QuantumProcessed(AudioGraph sender, object args)
    {
        if (output is null) return;
        using var frame = output.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        ((IMemoryBufferByteAccess)reference).GetBuffer(out var data, out var capacity);
        var sampleCount = Math.Min(1024, (int)capacity / sizeof(float));
        if (sampleCount < 64) return;

        var fftSize = 1;
        while (fftSize * 2 <= sampleCount) fftSize *= 2;
        var spectrum = new Complex[fftSize];
        var samples = (float*)data;
        for (var index = 0; index < fftSize; index++)
        {
            var window = 0.5 - 0.5 * Math.Cos(2 * Math.PI * index / (fftSize - 1));
            spectrum[index] = new Complex(samples[index] * window, 0);
        }
        Transform(spectrum);

        const int bucketCount = 24;
        var values = new double[bucketCount];
        var usableBins = fftSize / 2;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            var start = Math.Max(1, (int)Math.Pow(usableBins, bucket / (double)bucketCount));
            var end = Math.Max(start + 1, (int)Math.Pow(usableBins, (bucket + 1) / (double)bucketCount));
            end = Math.Min(usableBins, end);
            double peak = 0;
            for (var index = start; index < end; index++) peak = Math.Max(peak, spectrum[index].Magnitude);
            values[bucket] = Math.Clamp(Math.Log10(1 + peak) / 2.2, 0, 1);
        }
        SpectrumAvailable?.Invoke(this, values);
    }

    private static void Transform(Complex[] values)
    {
        var length = values.Length;
        var reversed = 0;
        for (var index = 1; index < length; index++)
        {
            var bit = length >> 1;
            for (; (reversed & bit) != 0; bit >>= 1) reversed ^= bit;
            reversed ^= bit;
            if (index < reversed) (values[index], values[reversed]) = (values[reversed], values[index]);
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            for (var offset = 0; offset < length; offset += size)
            {
                var phase = Complex.One;
                for (var index = 0; index < size / 2; index++)
                {
                    var even = values[offset + index];
                    var odd = values[offset + index + size / 2] * phase;
                    values[offset + index] = even + odd;
                    values[offset + index + size / 2] = even - odd;
                    phase *= step;
                }
            }
        }
    }

    private async Task ResetAsync()
    {
        if (graph is not null) graph.QuantumProcessed -= Graph_QuantumProcessed;
        input?.Stop();
        graph?.Stop();
        input?.Dispose();
        output?.Dispose();
        graph?.Dispose();
        input = null;
        output = null;
        graph = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await ResetAsync();

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-8657-1D0C7D64D2A0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
