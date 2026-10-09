using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Patchouli.UI.ViewModels.AgentChat;

public sealed partial class AgentChatMessageViewModel
{
    public long EffectId { get; }
    public string OperationId { get; }
    public string ParentId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFailure), nameof(StateLabel), nameof(RecoveryHint))]
    public partial string State { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInput))]
    public partial string Input { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutput))]
    public partial string Output { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorCode), nameof(RecoveryHint))]
    public partial string ErrorCode { get; set; } = "";

    [ObservableProperty] public partial string DurationText { get; set; } = "";
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    [ExcludeFromDerivedGeneration] public bool IsRunning => State == "running";
    [ExcludeFromDerivedGeneration] public bool IsFailure => State == "failed";
    [ExcludeFromDerivedGeneration] public bool HasInput => Input.Length > 0;
    [ExcludeFromDerivedGeneration] public bool HasOutput => Output.Length > 0;
    [ExcludeFromDerivedGeneration] public bool HasErrorCode => ErrorCode.Length > 0;

    [ExcludeFromDerivedGeneration]
    public string StepLabel => OperationId.Length > 0 ? $"SDK {OperationId} · {ParentId}" :
        EffectId > 0 ? $"步骤 {EffectId:00}" : $"事件 {Seq:00}";

    [ExcludeFromDerivedGeneration]
    public string StateLabel => State switch
    {
        "running" => "执行中", "planned" => "计划调用", "failed" => "失败", "completed" => "完成", "stopped" => "已停止", _ => "详情"
    };

    [ExcludeFromDerivedGeneration] public string Summary => Text.Split('\n', 2)[0].Trim();

    [ExcludeFromDerivedGeneration]
    public string RecoveryHint => ErrorCode == "auth_failed" || Text.Contains("auth_failed", StringComparison.Ordinal)
        ? "请在 LLM 设置中检查所选 provider 的凭据，然后在下方发送消息继续。"
        : ErrorCode is "SDK_OPERATION_UNKNOWN" or "FSI_EXECUTION_INTERRUPTED"
            ? "请先核对目标资源和已记录操作；未确认的写入不会自动重试。核对后可在新会话继续，FSI 临时变量需重新建立。"
            : ErrorCode == "PERMISSION_DENIED"
                ? "请检查 MCP 权限矩阵，再发送消息继续。"
                : "失败不会关闭对话。可在下方补充说明，修正后继续。";

    [ExcludeFromDerivedGeneration]
    public string DetailText =>
        $"{Title}\n{StateLabel} {DurationText}\n{ErrorCode}\n\n输入\n{Input}\n\n输出\n{(HasOutput ? Output : Text)}";

    internal void UpdateActivity(AgentChatMessage message)
    {
        // A raw tool event supplements its authoritative outcome without overwriting failure state.
        if (message.State.Length > 0)
        {
            State = message.State;
        }

        if (message.Input.Length > 0)
        {
            Input = Pretty(message.Input);
        }

        if (message.Output.Length > 0)
        {
            Output = Pretty(message.Output);
        }

        if (message.ErrorCode.Length > 0)
        {
            ErrorCode = message.ErrorCode;
        }

        if (message.State.Length > 0)
        {
            Text = message.Text;
            if (message.ElapsedMs > 0)
            {
                DurationText = message.ElapsedMs < 1000
                    ? $"{message.ElapsedMs:0} ms"
                    : $"{message.ElapsedMs / 1000:0.0} s";
            }
        }

        if (IsFailure)
        {
            IsExpanded = true;
        }
    }

    private static string Pretty(string value)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(value);
            return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        }
        catch (JsonException)
        {
            return value;
        }
    }
}
