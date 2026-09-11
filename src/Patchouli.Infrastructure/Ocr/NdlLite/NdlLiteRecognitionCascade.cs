using System.Runtime.ExceptionServices;
using SkiaSharp;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>One line crop to recognize together with the detector's predicted character count.</summary>
public sealed record NdlLiteRecognitionInput(SKBitmap Image, float PredictedCharCount, int Index);

/// <summary>
/// Native port of the official three-tier PARSeq cascade in <c>ocr.py</c>.
/// The small 24x256 model handles short lines, the 24x384 model handles medium
/// lines, and the 24x768 model handles long lines; the detector's
/// <c>PRED_CHAR_CNT</c> selects the entry tier and each tier escalates when its
/// decoded string overflows. Very long horizontal lines are split in half and
/// re-read with the large model, exactly as upstream does.
/// </summary>
public sealed class NdlLiteRecognitionCascade : IDisposable
{
    /// <summary>Official escalation threshold after the 24x256 tier.</summary>
    public const int Tier30OverflowThreshold = 25;

    /// <summary>Official escalation threshold after the 24x384 tier.</summary>
    public const int Tier50OverflowThreshold = 45;

    /// <summary>Official split threshold inside the 24x768 tier.</summary>
    public const int Tier100SplitThreshold = 98;

    /// <summary>Detector <c>PRED_CHAR_CNT</c> that starts in the 24x256 tier.</summary>
    public const float Tier30PredictedCharCount = 3.0f;

    /// <summary>Detector <c>PRED_CHAR_CNT</c> that starts in the 24x384 tier.</summary>
    public const float Tier50PredictedCharCount = 2.0f;

    private const int Tier30 = 30;
    private const int Tier50 = 50;
    private const int Tier100 = 100;

    private readonly NdlLiteRecognizer _tier30Recognizer;
    private readonly NdlLiteRecognizer _tier50Recognizer;
    private readonly NdlLiteRecognizer _tier100Recognizer;

    public NdlLiteRecognitionCascade(NdlLiteRecognizer tier30Recognizer, NdlLiteRecognizer tier50Recognizer,
        NdlLiteRecognizer tier100Recognizer, int? workerCount = null)
    {
        _tier30Recognizer = tier30Recognizer;
        _tier50Recognizer = tier50Recognizer;
        _tier100Recognizer = tier100Recognizer;
        WorkerCount = workerCount ?? DefaultWorkerCount;
        if (WorkerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker count must be at least one.");
        }
    }

    /// <summary>
    /// Python's <c>ThreadPoolExecutor</c> default: <c>min(32, cpu_count + 4)</c>.
    /// The official cascade uses the default executor.
    /// </summary>
    public static int DefaultWorkerCount => Math.Max(1, Math.Min(32, Environment.ProcessorCount + 4));

    public int WorkerCount { get; }

    /// <summary>Maps a detector <c>PRED_CHAR_CNT</c> to the first model to run.</summary>
    public static int SelectInitialTier(float predictedCharCount, bool isCascade)
    {
        if (!isCascade)
        {
            return Tier100;
        }

        if (predictedCharCount == Tier30PredictedCharCount)
        {
            return Tier30;
        }

        return predictedCharCount == Tier50PredictedCharCount ? Tier50 : Tier100;
    }

    public IReadOnlyList<string> Recognize(IReadOnlyList<NdlLiteRecognitionInput> inputs, bool isCascade = true)
    {
        string[] results = new string[inputs.Count];
        List<Job> tier30Jobs = new();
        List<Job> tier50Jobs = new();
        List<Job> tier100Jobs = new();
        foreach (NdlLiteRecognitionInput input in inputs)
        {
            Job job = new(input.Index, input.Image, false);
            switch (SelectInitialTier(input.PredictedCharCount, isCascade))
            {
                case Tier30:
                    tier30Jobs.Add(job);
                    break;
                case Tier50:
                    tier50Jobs.Add(job);
                    break;
                default:
                    tier100Jobs.Add(job);
                    break;
            }
        }

        tier50Jobs.AddRange(RunTier(tier30Jobs, _tier30Recognizer, results, Tier30OverflowThreshold));
        tier100Jobs.AddRange(RunTier(tier50Jobs, _tier50Recognizer, results, Tier50OverflowThreshold));
        RunTier100(tier100Jobs, results);
        return results;
    }

    private List<Job> RunTier(IReadOnlyList<Job> jobs, NdlLiteRecognizer recognizer, string[] results,
        int overflowThreshold)
    {
        List<Job> overflow = new();
        if (jobs.Count == 0)
        {
            return overflow;
        }

        Job[] ordered = jobs.ToArray();
        string[] texts = new string[ordered.Length];
        RunParallel(ordered.Length, WorkerCount, i => texts[i] = recognizer.Read(ordered[i].Image));

        for (int i = 0; i < ordered.Length; i++)
        {
            if (texts[i].Length >= overflowThreshold)
            {
                overflow.Add(ordered[i]);
            }
            else
            {
                results[ordered[i].Slot] = texts[i];
            }
        }

        return overflow;
    }

    private void RunTier100(IReadOnlyList<Job> jobs, string[] results)
    {
        if (jobs.Count == 0)
        {
            return;
        }

        Job[] ordered = jobs.ToArray();
        string[] texts = new string[ordered.Length];
        RunParallel(ordered.Length, WorkerCount, i => texts[i] = _tier100Recognizer.Read(ordered[i].Image));

        List<Job> splitJobs = new();
        for (int i = 0; i < ordered.Length; i++)
        {
            Job job = ordered[i];
            string text = texts[i];
            if (text.Length >= Tier100SplitThreshold && job.Image.Height < job.Image.Width)
            {
                (SKBitmap left, SKBitmap right) = CropHalves(job.Image);
                splitJobs.Add(new Job(job.Slot, left, true));
                splitJobs.Add(new Job(job.Slot, right, true));
            }
            else
            {
                results[job.Slot] = text;
            }
        }

        if (splitJobs.Count == 0)
        {
            return;
        }

        Job[] halves = splitJobs.ToArray();
        string[] halfTexts = new string[halves.Length];
        try
        {
            RunParallel(halves.Length, WorkerCount,
                i => halfTexts[i] = _tier100Recognizer.Read(halves[i].Image));
            for (int i = 0; i + 1 < halves.Length; i += 2)
            {
                results[halves[i].Slot] = halfTexts[i] + halfTexts[i + 1];
            }
        }
        finally
        {
            foreach (Job half in halves)
            {
                if (half.OwnsImage)
                {
                    half.Image.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Runs one worker stage of the cascade. <see cref="Parallel.For(int,int,ParallelOptions,Action{int})"/>
    /// wraps worker failures in an <see cref="AggregateException"/>; the first inner
    /// exception is rethrown with its original stack so callers see the typed
    /// failure (for example a corrupt ONNX session or an invalid decode) instead of
    /// a generic worker crash.
    /// </summary>
    internal static void RunParallel(int count, int workerCount, Action<int> body)
    {
        if (count <= 0)
        {
            return;
        }

        try
        {
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workerCount }, body);
        }
        catch (AggregateException exception) when (exception.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }
    }

    private static (SKBitmap Left, SKBitmap Right) CropHalves(SKBitmap image)
    {
        int width = image.Width;
        int height = image.Height;
        int half = width / 2;
        SKBitmap left = new(half, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        SKBitmap right = new(width - half, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (SKCanvas leftCanvas = new(left))
        {
            leftCanvas.DrawBitmap(image, new SKRect(0, 0, half, height), new SKRect(0, 0, half, height));
        }

        using (SKCanvas rightCanvas = new(right))
        {
            rightCanvas.DrawBitmap(image, new SKRect(half, 0, width, height), new SKRect(0, 0, width - half, height));
        }

        return (left, right);
    }

    public void Dispose()
    {
        _tier30Recognizer.Dispose();
        _tier50Recognizer.Dispose();
        _tier100Recognizer.Dispose();
    }

    private sealed record Job(int Slot, SKBitmap Image, bool OwnsImage);
}
