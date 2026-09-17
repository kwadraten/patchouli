using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;

namespace Patchouli.UI.Controls.CatWalk;

public sealed class CatGeometrySet
{
    public Geometry IdleGeometry { get; }
    public IReadOnlyList<Geometry> ActiveGeometries { get; }
    public Rect SharedBounds { get; }

    public CatGeometrySet(Geometry idleGeometry, IReadOnlyList<Geometry> activeGeometries)
    {
        IdleGeometry = idleGeometry ?? throw new ArgumentNullException(nameof(idleGeometry));
        ActiveGeometries = activeGeometries ?? throw new ArgumentNullException(nameof(activeGeometries));
        if (activeGeometries.Count != 5)
        {
            throw new ArgumentException("Expected exactly 5 active frames.", nameof(activeGeometries));
        }

        Rect bounds = idleGeometry.Bounds;
        for (int i = 0; i < activeGeometries.Count; i++)
        {
            bounds = bounds.Union(activeGeometries[i].Bounds);
        }

        SharedBounds = bounds;
    }
}

public static class CatGeometryProvider
{
    private static readonly Lazy<CatGeometrySet> LazyInstance = new(
        LoadGeometries,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public static CatGeometrySet Instance => LazyInstance.Value;

    public static CatGeometrySet LoadGeometries()
    {
        Geometry idle = LoadFrame("my-idle-symbolic.svg");
        Geometry[] active = new Geometry[5];
        for (int i = 0; i < 5; i++)
        {
            active[i] = LoadFrame($"my-active-{i}-symbolic.svg");
        }

        return new CatGeometrySet(idle, Array.AsReadOnly(active));
    }

    public static Geometry LoadFrame(string fileName)
    {
        using Stream stream = OpenAssetStream(fileName);
        XDocument doc = XDocument.Load(stream);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        XElement? pathElement = doc.Root?.Descendants(ns + "path").FirstOrDefault()
                                ?? doc.Root?.Descendants("path").FirstOrDefault()
                                ?? throw new InvalidOperationException($"No path element found in {fileName}");
        string? d = (string?)pathElement.Attribute("d");
        if (string.IsNullOrWhiteSpace(d))
        {
            throw new InvalidOperationException($"No 'd' attribute found on path in {fileName}");
        }

        return StreamGeometry.Parse(d);
    }

    private static Stream OpenAssetStream(string fileName)
    {
        Uri uri = new($"avares://Patchouli.UI/Assets/CatWalk/{fileName}");
        try
        {
            if (AssetLoader.Exists(uri))
            {
                return AssetLoader.Open(uri);
            }
        }
        catch (Exception exception)
        {
            // Headless unit tests may not have initialized Avalonia's asset loader.
            System.Diagnostics.Debug.WriteLine($"CatWalk resource lookup fell back to disk: {exception}");
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, "src", "Patchouli.UI", "Assets", "CatWalk", fileName);
            if (File.Exists(candidate))
            {
                return File.OpenRead(candidate);
            }

            candidate = Path.Combine(current.FullName, "Assets", "CatWalk", fileName);
            if (File.Exists(candidate))
            {
                return File.OpenRead(candidate);
            }

            if (File.Exists(Path.Combine(current.FullName, "Patchouli.sln")))
            {
                string slnCandidate =
                    Path.Combine(current.FullName, "src", "Patchouli.UI", "Assets", "CatWalk", fileName);
                if (File.Exists(slnCandidate))
                {
                    return File.OpenRead(slnCandidate);
                }

                break;
            }

            current = current.Parent;
        }

        return AssetLoader.Open(uri);
    }
}
