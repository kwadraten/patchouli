using System.IO;
using System.Xml;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;

namespace Patchouli.UI.Controls;

/// <summary>FSX source editor using the shared two-way editor binding and an F# syntax definition.</summary>
public sealed class FSharpSourceEditor : LineNumberedTextEditor
{
    private static readonly IHighlightingDefinition FSharpHighlighting = CreateHighlighting();

    public FSharpSourceEditor()
    {
        WordWrap = false;
        SyntaxHighlighting = FSharpHighlighting;
    }

    private static IHighlightingDefinition CreateHighlighting()
    {
        const string definition = """
                                  <SyntaxDefinition name="F#" extensions=".fs;.fsx;.fsi" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
                                    <Color name="Comment" foreground="#738A69" />
                                    <Color name="String" foreground="#B47B43" />
                                    <Color name="Keyword" foreground="#9575D4" fontWeight="bold" />
                                    <Color name="Number" foreground="#5798AC" />
                                    <RuleSet>
                                      <Span color="Comment" begin="//" />
                                      <Span color="Comment" multiline="true" begin="\(\*" end="\*\)" />
                                      <Span color="String" begin="&quot;" end="&quot;">
                                        <RuleSet><Span begin="\\" end="." /></RuleSet>
                                      </Span>
                                      <Keywords color="Keyword">
                                        <Word>let</Word><Word>rec</Word><Word>mutable</Word><Word>use</Word><Word>in</Word>
                                        <Word>open</Word><Word>module</Word><Word>namespace</Word><Word>type</Word><Word>member</Word>
                                        <Word>interface</Word><Word>with</Word><Word>inherit</Word><Word>override</Word><Word>abstract</Word>
                                        <Word>new</Word><Word>of</Word><Word>as</Word><Word>and</Word><Word>function</Word><Word>fun</Word>
                                        <Word>match</Word><Word>when</Word><Word>if</Word><Word>then</Word><Word>else</Word><Word>elif</Word>
                                        <Word>for</Word><Word>while</Word><Word>do</Word><Word>to</Word><Word>downto</Word><Word>yield</Word>
                                        <Word>return</Word><Word>try</Word><Word>finally</Word><Word>raise</Word><Word>failwith</Word>
                                        <Word>true</Word><Word>false</Word><Word>null</Word><Word>async</Word><Word>task</Word>
                                        <Word>public</Word><Word>private</Word><Word>internal</Word><Word>inline</Word><Word>static</Word>
                                      </Keywords>
                                      <Rule color="Number">\b[0-9]+(\.[0-9]+)?\b</Rule>
                                      <Rule color="Keyword">^\s*\#(r|load|I|nowarn)\b</Rule>
                                    </RuleSet>
                                  </SyntaxDefinition>
                                  """;
        using StringReader text = new(definition);
        using XmlReader reader = XmlReader.Create(text);
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }
}
