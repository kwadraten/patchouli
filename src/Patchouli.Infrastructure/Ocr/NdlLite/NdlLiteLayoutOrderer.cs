namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Native port of the NDLOCR-Lite layout grouping and line ordering that
/// <c>convert_to_xml_string3</c> plus <c>reading_order/order/reorder.py</c> apply
/// after XY-cut ranking: text blocks adopt their contained lines, nested blocks
/// are folded into their parent, then page children are sorted by their median
/// XY-cut order and each text block sorts its own lines top-to-bottom (horizontal)
/// or right-to-left (vertical).
/// </summary>
/// <remarks>
/// Two upstream <c>sort_lines</c> stages are intentionally not reproduced:
/// <list type="bullet">
/// <item>
/// <c>GroupWarichu</c> (割注 grouping into <c>WARICHUBLOCK</c>) is omitted: this
/// port orders the flat line set that the pipeline recognizes, so there is no
/// warichu container to group.
/// </item>
/// <item>
/// <c>smooth_order</c> (the minimum-Hamiltonian-path refinement) is omitted: it
/// requires an unbounded path search over the page graph and only nudges already
/// ordered neighbours, so the deterministic XY-cut order and local line sort are
/// used instead.
/// </item>
/// </list>
/// </remarks>
public static class NdlLiteLayoutOrderer
{
    /// <summary>Official <c>score_thr</c> used when emitting lines into the XML.</summary>
    public const float LineConfidenceThreshold = 0.1f;

    /// <summary>Official <c>min_bbox_size</c> for text blocks and synthesized lines.</summary>
    public const int MinimumBoxSize = 5;

    /// <summary>Official overlap ratio above which two lines are treated as duplicates.</summary>
    public const double DuplicateOverlapRatio = 0.8;

    /// <summary>Official <c>refine_tb_relationship</c> containment margin.</summary>
    public const double TextBlockContainmentMargin = 50.0;

    private const string TextBlockName = "text_block";
    private const string LineMainName = "line_main";
    private const string BlockAdName = "block_ad";
    private const string BlockTableName = "block_table";

    public static IReadOnlyList<NdlLiteDetection> Order(IReadOnlyList<NdlLiteDetection> detections)
    {
        return Order(detections, out _);
    }

    internal static IReadOnlyList<NdlLiteDetection> Order(IReadOnlyList<NdlLiteDetection> detections,
        out IReadOnlyList<int> ranks)
    {
        List<Container> textBlocks = new();
        List<Container> adBlocks = new();
        List<Container> tableBlocks = new();
        List<LineItem> independentLines = new();

        List<NdlLiteDetection> lines = new();
        foreach (NdlLiteDetection detection in detections)
        {
            if (IsLine(detection) && detection.Confidence >= LineConfidenceThreshold)
            {
                lines.Add(detection);
            }
        }

        BuildContainers(detections, textBlocks, adBlocks, tableBlocks);
        AssignLines(lines, textBlocks, adBlocks, tableBlocks, independentLines);
        FoldNestedTextBlocks(textBlocks);

        List<LineItem> emission = BuildEmissionOrder(textBlocks, adBlocks, tableBlocks, independentLines);
        int[] computedRanks = NdlLiteReadingOrderSolver.Solve(emission.Select(static item => item.Detection.Box)
            .ToArray());
        for (int i = 0; i < emission.Count; i++)
        {
            emission[i].Rank = computedRanks[i];
        }

        ranks = emission.Select(static item => item.Rank).ToArray();
        List<NdlLiteDetection> ordered = BuildPageOrder(textBlocks, adBlocks, tableBlocks, independentLines);
        return ordered;
    }

    private static void BuildContainers(IReadOnlyList<NdlLiteDetection> detections, List<Container> textBlocks,
        List<Container> adBlocks, List<Container> tableBlocks)
    {
        for (int i = 0; i < detections.Count; i++)
        {
            NdlLiteDetection detection = detections[i];
            if (string.Equals(detection.ClassName, TextBlockName, StringComparison.Ordinal))
            {
                if (detection.Box.Width < MinimumBoxSize && detection.Box.Height < MinimumBoxSize)
                {
                    continue;
                }

                textBlocks.Add(new Container(detection.Box, i, detection.Confidence));
            }
            else if (string.Equals(detection.ClassName, BlockAdName, StringComparison.Ordinal))
            {
                adBlocks.Add(new Container(detection.Box, i, detection.Confidence));
            }
            else if (string.Equals(detection.ClassName, BlockTableName, StringComparison.Ordinal))
            {
                tableBlocks.Add(new Container(detection.Box, i, detection.Confidence));
            }
        }
    }

    private static void AssignLines(IReadOnlyList<NdlLiteDetection> lines, List<Container> textBlocks,
        List<Container> adBlocks, List<Container> tableBlocks, List<LineItem> independentLines)
    {
        // Upstream get_relationship_rect iterates classes in id order, then lines
        // within a class in detection order. OrderBy is stable, so this reproduces
        // the class-major assignment and independent-line emission order.
        foreach (NdlLiteDetection line in lines.OrderBy(static detection => detection.ClassIndex))
        {
            bool assigned = false;
            foreach (Container textBlock in textBlocks)
            {
                if (textBlock.Box.Contains(line.Box.CenterX, line.Box.CenterY))
                {
                    textBlock.Lines.Add(new LineItem(line));
                    assigned = true;
                    break;
                }
            }

            if (!assigned)
            {
                foreach (Container adBlock in adBlocks)
                {
                    if (adBlock.Box.Contains(line.Box.CenterX, line.Box.CenterY))
                    {
                        adBlock.Lines.Add(new LineItem(line));
                        assigned = true;
                        break;
                    }
                }
            }

            if (!assigned)
            {
                foreach (Container tableBlock in tableBlocks)
                {
                    if (tableBlock.Box.Contains(line.Box.CenterX, line.Box.CenterY))
                    {
                        tableBlock.Lines.Add(new LineItem(line));
                        assigned = true;
                        break;
                    }
                }
            }

            if (!assigned)
            {
                independentLines.Add(new LineItem(line));
            }
        }
    }

    private static void FoldNestedTextBlocks(List<Container> textBlocks)
    {
        for (int childIndex = 0; childIndex < textBlocks.Count; childIndex++)
        {
            Container? child = textBlocks[childIndex];
            if (child.Removed)
            {
                continue;
            }

            for (int parentIndex = 0; parentIndex < textBlocks.Count; parentIndex++)
            {
                if (childIndex == parentIndex)
                {
                    continue;
                }

                Container parent = textBlocks[parentIndex];
                if (parent.Removed)
                {
                    continue;
                }

                if (!IsInsideWithMargin(child.Box, parent.Box, TextBlockContainmentMargin))
                {
                    continue;
                }

                if (child.Lines.Count == 0)
                {
                    parent.Children.Add(child);
                    parent.Lines.Add(LineItem.CreateSynthetic(child.Box, child.Confidence, childIndex));
                }
                else
                {
                    parent.Lines.AddRange(child.Lines);
                    parent.Children.Add(child);
                }

                child.Removed = true;
                break;
            }
        }
    }

    private static List<LineItem> BuildEmissionOrder(List<Container> textBlocks, List<Container> adBlocks,
        List<Container> tableBlocks, List<LineItem> independentLines)
    {
        List<LineItem> emission = new();

        foreach (Container tableBlock in tableBlocks)
        {
            AppendContainerLines(tableBlock, emission);
        }

        foreach (Container adBlock in adBlocks)
        {
            foreach (Container child in adBlock.Children)
            {
                AppendContainerLines(child, emission);
            }

            AppendLines(adBlock.Lines, emission);
        }

        foreach (Container textBlock in textBlocks)
        {
            if (textBlock.Removed)
            {
                continue;
            }

            AppendContainerLines(textBlock, emission);
        }

        AppendLines(independentLines, emission);
        return emission;
    }

    private static void AppendContainerLines(Container container, List<LineItem> emission)
    {
        if (container.Lines.Count > 0)
        {
            AppendLines(container.Lines, emission);
            return;
        }

        if (container.Box.Width >= MinimumBoxSize && container.Box.Height >= MinimumBoxSize)
        {
            LineItem synthetic = LineItem.CreateSynthetic(container.Box, container.Confidence,
                container.SourceIndex);
            container.Lines.Add(synthetic);
            emission.Add(synthetic);
        }
    }

    private static void AppendLines(List<LineItem> source, List<LineItem> emission)
    {
        foreach (LineItem item in source)
        {
            emission.Add(item);
        }
    }

    private static List<NdlLiteDetection> BuildPageOrder(List<Container> textBlocks, List<Container> adBlocks,
        List<Container> tableBlocks, List<LineItem> independentLines)
    {
        List<SortableItem> sortable = new();

        foreach (Container textBlock in textBlocks)
        {
            if (textBlock.Removed || textBlock.Lines.Count == 0)
            {
                continue;
            }

            SortLinesLocal(textBlock.Lines);
            sortable.Add(new SortableItem(MedianOrder(textBlock.Lines), textBlock.Lines));
        }

        sortable.AddRange(independentLines.Select(static item =>
            new SortableItem(item.Rank, new List<LineItem> { item })));

        SortableItem[] sortedItems = sortable.OrderBy(static item => item.Order).ToArray();

        List<NdlLiteDetection> ordered = new();
        foreach (SortableItem item in sortedItems)
        {
            foreach (LineItem line in item.Lines)
            {
                ordered.Add(line.Detection);
            }
        }

        // BLOCK children (tables and ads) are appended after the sortable
        // TEXTBLOCK/LINE children, matching sort_lines' unsorted tail.
        foreach (Container block in tableBlocks.Concat(adBlocks))
        {
            List<LineItem> blockLines = new();
            foreach (Container child in block.Children)
            {
                SortLinesLocal(child.Lines);
                blockLines.AddRange(child.Lines);
            }

            SortLinesLocal(block.Lines);
            blockLines.AddRange(block.Lines);
            foreach (LineItem line in blockLines)
            {
                ordered.Add(line.Detection);
            }
        }

        return ordered;
    }

    private static double MedianOrder(List<LineItem> lines)
    {
        if (lines.Count == 0)
        {
            return double.NaN;
        }

        List<double> orders = lines.Select(static item => (double)item.Rank).ToList();
        orders.Sort();
        return orders[orders.Count / 2];
    }

    private static void SortLinesLocal(List<LineItem> lines)
    {
        if (lines.Count <= 1)
        {
            return;
        }

        int vertical = lines.Count(static item => item.Detection.Box.Width < item.Detection.Box.Height);
        bool isVertical = lines.Count < vertical * 2;
        double spanMedian = isVertical
            ? Median(lines.Select(static item => (double)item.Detection.Box.Width))
            : Median(lines.Select(static item => (double)item.Detection.Box.Height));
        double margin = spanMedian * 0.3;

        // LINQ OrderBy is a stable sort, matching upstream's stable sorted();
        // List<T>.Sort would reorder equal-centre lines nondeterministically.
        LineItem[] sorted = lines
            .OrderBy(static item => item, new LocalLineComparer(isVertical, margin))
            .ToArray();
        lines.Clear();
        lines.AddRange(sorted);

        RemoveDuplicates(lines);
    }

    private sealed class LocalLineComparer : IComparer<LineItem>
    {
        private readonly bool _isVertical;
        private readonly double _margin;

        public LocalLineComparer(bool isVertical, double margin)
        {
            _isVertical = isVertical;
            _margin = margin;
        }

        public int Compare(LineItem? x, LineItem? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return CompareLocalLines(x, y, _isVertical, _margin);
        }
    }

    private static int CompareLocalLines(LineItem left, LineItem right, bool isVertical, double margin)
    {
        NdlLiteBox a = left.Detection.Box;
        NdlLiteBox b = right.Detection.Box;
        double ax = a.X0 + a.Width / 2.0;
        double ay = a.Y0 + a.Height / 2.0;
        double bx = b.X0 + b.Width / 2.0;
        double by = b.Y0 + b.Height / 2.0;
        if (isVertical)
        {
            if (margin < bx - ax)
            {
                return 1;
            }

            if (margin < ax - bx)
            {
                return -1;
            }

            return ay.CompareTo(by);
        }

        if (margin < ay - by)
        {
            return 1;
        }

        if (margin < by - ay)
        {
            return -1;
        }

        return ax.CompareTo(bx);
    }

    private static void RemoveDuplicates(List<LineItem> lines)
    {
        for (int i = 1; i < lines.Count;)
        {
            if (!OverlapsEnough(lines[i - 1].Detection, lines[i].Detection))
            {
                i++;
                continue;
            }

            // Upstream compares the CONF attribute that was already written with
            // f"{conf:0.3f}", so compare confidence rounded to three decimals.
            if (RoundConfidence(lines[i - 1].Detection.Confidence)
                >= RoundConfidence(lines[i].Detection.Confidence))
            {
                lines.RemoveAt(i);
            }
            else
            {
                lines.RemoveAt(i - 1);
            }
        }
    }

    private static float RoundConfidence(float confidence)
    {
        return MathF.Round(confidence, 3);
    }

    /// <summary>
    /// Official <c>check_iou</c>: duplicates when the intersection covers more
    /// than <see cref="DuplicateOverlapRatio"/> of the smaller box.
    /// </summary>
    internal static bool OverlapsEnough(NdlLiteDetection first, NdlLiteDetection second)
    {
        NdlLiteBox a = first.Box;
        NdlLiteBox b = second.Box;
        double areaA = (double)Math.Max(0, a.Width) * Math.Max(0, a.Height);
        double areaB = (double)Math.Max(0, b.Width) * Math.Max(0, b.Height);
        int x0 = Math.Max(a.X0, b.X0);
        int y0 = Math.Max(a.Y0, b.Y0);
        int x1 = Math.Min(a.X1, b.X1);
        int y1 = Math.Min(a.Y1, b.Y1);
        double intersection = (double)Math.Max(0, x1 - x0) * Math.Max(0, y1 - y0);
        double minArea = Math.Min(areaA, areaB);
        return minArea > 0 && intersection / minArea > DuplicateOverlapRatio;
    }

    private static bool IsInsideWithMargin(NdlLiteBox inner, NdlLiteBox outer, double margin)
    {
        return outer.X0 - margin <= inner.X0
               && inner.X1 <= outer.X1 + margin
               && outer.Y0 - margin <= inner.Y0
               && inner.Y1 <= outer.Y1 + margin;
    }

    private static bool IsLine(NdlLiteDetection detection)
    {
        return detection.ClassName.StartsWith("line_", StringComparison.Ordinal);
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = values.ToList();
        if (sorted.Count == 0)
        {
            return 0.0;
        }

        sorted.Sort();
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed class Container
    {
        public Container(NdlLiteBox box, int sourceIndex, float confidence)
        {
            Box = box;
            SourceIndex = sourceIndex;
            Confidence = confidence;
        }

        public NdlLiteBox Box { get; }
        public int SourceIndex { get; }
        public float Confidence { get; }
        public List<LineItem> Lines { get; } = new();
        public List<Container> Children { get; } = new();
        public bool Removed { get; set; }
    }

    private sealed class LineItem
    {
        public LineItem(NdlLiteDetection detection)
        {
            Detection = detection;
        }

        public NdlLiteDetection Detection { get; }
        public int Rank { get; set; } = -1;

        public static LineItem CreateSynthetic(NdlLiteBox box, float confidence, int sourceIndex)
        {
            return new LineItem(new NdlLiteDetection(NdlLiteClassNames.LineMainIndex, LineMainName, confidence, box,
                100.0f));
        }
    }

    private sealed record SortableItem(double Order, List<LineItem> Lines);
}
