namespace Patchouli.Evidence;

public readonly record struct EvidenceOcrLineRange(int StartLine, int EndLine)
{
    public override string ToString() => StartLine == EndLine
        ? StartLine.ToString()
        : $"{StartLine}-{EndLine}";
}
