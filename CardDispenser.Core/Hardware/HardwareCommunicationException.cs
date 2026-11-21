namespace CardDispenser.Core.Hardware;

/// <summary>
/// 模擬「與發卡機控制板通訊逾時/中斷」的情境。
/// 業務層(CheckInCardIssuer)捕捉此例外後,必須讓流程收斂到安全狀態並中止交易,
/// 不可讓交易卡在中間狀態、也不可無限重試通訊。
/// </summary>
public sealed class HardwareCommunicationException : Exception
{
    public HardwareCommunicationException(string message) : base(message)
    {
    }
}
