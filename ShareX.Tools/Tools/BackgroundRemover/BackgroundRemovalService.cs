#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;
using System.Diagnostics;

namespace ShareX.Tools;

public sealed class BackgroundRemovalModel
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }

    public string DisplayName => $"{FileName} ({FormatFileSize(FileSize)})";

    public override string ToString() => DisplayName;

    private static string FormatFileSize(long size)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = size;
        int unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return unitIndex == 0 ? $"{value:0} {units[unitIndex]}" : $"{value:0.#} {units[unitIndex]}";
    }
}

public sealed record BackgroundRemovalResult(
    SKBitmap Image,
    bool IsSessionCached,
    long SessionSetupMilliseconds,
    long PreprocessingMilliseconds,
    long InferenceMilliseconds,
    long PostprocessingMilliseconds);

public sealed class BackgroundRemovalService : IDisposable
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] StandardDeviation = [0.229f, 0.224f, 0.225f];
    // A wrong model can declare any size, and allocating for it would run out of memory before the runtime could refuse it.
    private const int MaxModelInputSide = 4096;
    private const string ModelDoesNotFit = "The model could not process the image. Use a background removal model that takes one RGB image and returns a mask.";
    private readonly Lock _sessionLock = new();
    private SessionCacheKey? _sessionKey;
    private InferenceSession? _session;
    private bool _isDisposed;

    public BackgroundRemovalResult RemoveBackground(SKBitmap source, BackgroundRemovalModel model)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(model);

        if (source.Width <= 0 || source.Height <= 0)
        {
            throw new InvalidOperationException("Selected image is empty.");
        }

        string modelPath = model.FilePath;
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The background removal model file is missing.", modelPath);
        }

        lock (_sessionLock)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            Stopwatch sessionStopwatch = Stopwatch.StartNew();
            SessionLookupResult sessionResult = GetOrCreateSession(modelPath);
            sessionStopwatch.Stop();
            return RunModel(source, sessionResult, sessionStopwatch.ElapsedMilliseconds);
        }
    }

    private SessionLookupResult GetOrCreateSession(string modelPath)
    {
        FileInfo modelFile = new(modelPath);
        SessionCacheKey cacheKey = new(modelFile.FullName, modelFile.Length, modelFile.LastWriteTimeUtc.Ticks);

        if (_sessionKey == cacheKey && _session != null)
        {
            return new SessionLookupResult(_session, true);
        }

        // Cleared before loading: if the new file fails, the old key must not match a disposed session on the next run.
        _session?.Dispose();
        _session = null;
        _sessionKey = null;

        _session = CreateSession(modelPath);
        _sessionKey = cacheKey;
        return new SessionLookupResult(_session, false);
    }

    private static InferenceSession CreateSession(string modelPath)
    {
        try
        {
            return new InferenceSession(modelPath);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new InvalidOperationException($"'{Path.GetFileName(modelPath)}' could not be loaded. The file is damaged or is not an ONNX model.", ex);
        }
    }

    private static BackgroundRemovalResult RunModel(SKBitmap source, SessionLookupResult sessionResult, long sessionSetupMilliseconds)
    {
        InferenceSession session = sessionResult.Session;
        string inputName = session.InputMetadata.Keys.FirstOrDefault()
            ?? throw new InvalidOperationException("The ONNX model does not expose an input tensor.");

        int inputWidth;
        int inputHeight;
        bool channelsLast;
        ResolveInputShape(session.InputMetadata[inputName].Dimensions, out inputWidth, out inputHeight, out channelsLast);

        Stopwatch phaseStopwatch = Stopwatch.StartNew();
        using SKBitmap inputBitmap = ResizeBitmap(source, inputWidth, inputHeight);
        DenseTensor<float> inputTensor = CreateInputTensor(inputBitmap, channelsLast);
        phaseStopwatch.Stop();
        long preprocessingMilliseconds = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = RunSession(session, inputName, inputTensor);

        Tensor<float> outputTensor = outputs.FirstOrDefault()?.AsTensor<float>()
            ?? throw new InvalidOperationException(ModelDoesNotFit);
        phaseStopwatch.Stop();
        long inferenceMilliseconds = phaseStopwatch.ElapsedMilliseconds;

        phaseStopwatch.Restart();
        using SKBitmap modelMask = CreateMaskBitmap(outputTensor);
        using SKBitmap resizedMask = ResizeBitmap(modelMask, source.Width, source.Height);
        SKBitmap result = ApplyAlphaMask(source, resizedMask);
        phaseStopwatch.Stop();

        return new BackgroundRemovalResult(
            result,
            sessionResult.IsCached,
            sessionSetupMilliseconds,
            preprocessingMilliseconds,
            inferenceMilliseconds,
            phaseStopwatch.ElapsedMilliseconds);
    }

    private static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunSession(InferenceSession session, string inputName, DenseTensor<float> inputTensor)
    {
        try
        {
            return session.Run(
            [
                NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
            ]);
        }
        catch (OnnxRuntimeException ex)
        {
            throw new InvalidOperationException(ModelDoesNotFit, ex);
        }
    }

    public void Dispose()
    {
        lock (_sessionLock)
        {
            if (_isDisposed)
            {
                return;
            }

            _session?.Dispose();
            _session = null;
            _sessionKey = null;
            _isDisposed = true;
        }
    }

    private readonly record struct SessionLookupResult(InferenceSession Session, bool IsCached);

    private readonly record struct SessionCacheKey(
        string ModelPath,
        long FileSize,
        long LastWriteTimeUtcTicks);

    private static void ResolveInputShape(IReadOnlyList<int> dimensions, out int width, out int height, out bool channelsLast)
    {
        width = 1024;
        height = 1024;
        channelsLast = false;

        if (dimensions.Count != 4)
        {
            return;
        }

        int channelIndex = dimensions[1] == 3 ? 1 : dimensions[3] == 3 ? 3 : 1;
        channelsLast = channelIndex == 3;

        int heightIndex = channelsLast ? 1 : 2;
        int widthIndex = channelsLast ? 2 : 3;

        if (dimensions[heightIndex] > 0)
        {
            height = dimensions[heightIndex];
        }

        if (dimensions[widthIndex] > 0)
        {
            width = dimensions[widthIndex];
        }

        if (width > MaxModelInputSide || height > MaxModelInputSide)
        {
            throw new InvalidOperationException($"The model asks for a {width}x{height} image, which is too large. {ModelDoesNotFit}");
        }
    }

    private static unsafe DenseTensor<float> CreateInputTensor(SKBitmap bitmap, bool channelsLast)
    {
        DenseTensor<float> tensor = channelsLast
            ? new DenseTensor<float>([1, bitmap.Height, bitmap.Width, 3])
            : new DenseTensor<float>([1, 3, bitmap.Height, bitmap.Width]);
        Span<float> tensorValues = tensor.Buffer.Span;
        int pixelCount = bitmap.Width * bitmap.Height;
        byte* pixels = (byte*)bitmap.GetPixels();

        for (int y = 0; y < bitmap.Height; y++)
        {
            byte* row = pixels + (y * bitmap.RowBytes);

            for (int x = 0; x < bitmap.Width; x++)
            {
                byte* pixel = row + (x * 4);
                float alphaScale = pixel[3] is > 0 and < 255 ? 255f / pixel[3] : 1f;
                float r = ((Math.Min(255f, pixel[2] * alphaScale) / 255f) - Mean[0]) / StandardDeviation[0];
                float g = ((Math.Min(255f, pixel[1] * alphaScale) / 255f) - Mean[1]) / StandardDeviation[1];
                float b = ((Math.Min(255f, pixel[0] * alphaScale) / 255f) - Mean[2]) / StandardDeviation[2];
                int pixelIndex = (y * bitmap.Width) + x;

                if (channelsLast)
                {
                    int tensorIndex = pixelIndex * 3;
                    tensorValues[tensorIndex] = r;
                    tensorValues[tensorIndex + 1] = g;
                    tensorValues[tensorIndex + 2] = b;
                }
                else
                {
                    tensorValues[pixelIndex] = r;
                    tensorValues[pixelCount + pixelIndex] = g;
                    tensorValues[(pixelCount * 2) + pixelIndex] = b;
                }
            }
        }

        return tensor;
    }

    private static unsafe SKBitmap CreateMaskBitmap(Tensor<float> outputTensor)
    {
        if (!TryResolveMaskSize(outputTensor.Dimensions, out int width, out int height))
        {
            throw new InvalidOperationException(ModelDoesNotFit);
        }

        float[] values = outputTensor.ToArray();
        int pixelCount = width * height;

        float min = float.MaxValue;
        float max = float.MinValue;

        for (int i = 0; i < pixelCount; i++)
        {
            float value = values[i];
            if (value < min) min = value;
            if (value > max) max = value;
        }

        if (min < 0f)
        {
            min = float.MaxValue;
            max = float.MinValue;

            for (int i = 0; i < pixelCount; i++)
            {
                float value = 1f / (1f + MathF.Exp(-values[i]));
                values[i] = value;
                if (value < min) min = value;
                if (value > max) max = value;
            }
        }

        float range = Math.Max(0.000001f, max - min);
        SKBitmap mask = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte* maskPixels = (byte*)mask.GetPixels();

        for (int y = 0; y < height; y++)
        {
            byte* row = maskPixels + (y * mask.RowBytes);

            for (int x = 0; x < width; x++)
            {
                int index = (y * width) + x;
                float normalized = Math.Clamp((values[index] - min) / range, 0f, 1f);
                byte alpha = (byte)MathF.Round(normalized * 255f);
                byte* pixel = row + (x * 4);
                pixel[0] = 0;
                pixel[1] = 0;
                pixel[2] = 0;
                pixel[3] = alpha;
            }
        }

        return mask;
    }

    // Only a single-channel image is a mask. Reading one channel of a colour image, or stretching a row of class scores, would give nonsense alpha.
    internal static bool TryResolveMaskSize(ReadOnlySpan<int> dimensions, out int width, out int height)
    {
        (height, width) = dimensions switch
        {
            [var h, var w] => (h, w),
            [1, var h, var w] => (h, w),
            [1, 1, var h, var w] => (h, w),
            [1, var h, var w, 1] => (h, w),
            _ => (0, 0)
        };

        return width > 1 && height > 1;
    }

    private static SKBitmap ApplyAlphaMask(SKBitmap source, SKBitmap mask)
    {
        SKBitmap output = new(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using SKCanvas canvas = new(output);
        using SKImage sourceImage = SKImage.FromBitmap(source);
        using SKImage maskImage = SKImage.FromBitmap(mask);
        using SKPaint maskPaint = new() { BlendMode = SKBlendMode.DstIn };
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(sourceImage, 0, 0);
        canvas.DrawImage(maskImage, 0, 0, maskPaint);

        return output;
    }

    private static SKBitmap ResizeBitmap(SKBitmap source, int width, int height)
    {
        if (source.Width == width && source.Height == height &&
            source.ColorType == SKColorType.Bgra8888 &&
            source.AlphaType is SKAlphaType.Premul or SKAlphaType.Opaque)
        {
            return source.Copy();
        }

        SKBitmap resized = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));

        using SKCanvas canvas = new(resized);
        using SKPaint paint = new() { IsAntialias = true };
        using SKImage sourceImage = SKImage.FromBitmap(source);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawImage(sourceImage, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.CatmullRom), paint);

        return resized;
    }
}
