namespace LitSSHmcp.App.Controls;

/// <summary>
/// 聊天区「自动跟随到底部」的判定策略。
/// <para>
/// 用户一旦主动向上滚动就暂停跟随：任务仍在继续渲染（工具调用、思考、结果），但不再改变滚动位置；
/// 只有滚回真正的底部、手动点「回到底部」或发送新消息才恢复。这样可以边跑任务边翻历史。
/// </para>
/// </summary>
public static class ChatScrollPolicy
{
    /// <summary>
    /// 「已回到底部」的容差。用 2px 吸收 DPI / 子像素误差，避免因为 0.3px
    /// 的差距导致再也无法恢复跟随。
    /// </summary>
    public const double BottomTolerance = 2;

    /// <summary>内容不足一屏，或垂直偏移已经贴到底部。</summary>
    public static bool IsAtBottom(double verticalOffset, double viewportHeight, double extentHeight) =>
        extentHeight <= viewportHeight + 1
        || verticalOffset + viewportHeight >= extentHeight - BottomTolerance;

    /// <summary>
    /// 根据一次 <see cref="System.Windows.Controls.ScrollChangedEventArgs"/> 的结果推导新的跟随状态。
    /// </summary>
    /// <param name="following">事件发生前是否跟随底部。</param>
    /// <param name="verticalChange">垂直偏移变化量，向上滚动为负。</param>
    /// <param name="verticalOffset">当前垂直偏移。</param>
    /// <param name="viewportHeight">当前视口高度。</param>
    /// <param name="extentHeight">当前内容总高度。</param>
    /// <returns>事件之后是否继续跟随底部。</returns>
    public static bool Resolve(
        bool following,
        double verticalChange,
        double verticalOffset,
        double viewportHeight,
        double extentHeight)
    {
        // 严格以“是否真的贴到底部”为准：内容变高导致离底时，跟随者继续保持（调用方随后滚到底），
        // 暂停者则永远不会因为新内容被拉回去。
        if (IsAtBottom(verticalOffset, viewportHeight, extentHeight))
            return true;

        // 向上滚动立即暂停。即使同一次布局里内容高度也发生了变化（流式输出很常见），
        // 用户意图优先，避免视图被抢回底部。
        if (verticalChange < -0.5)
            return false;

        return following;
    }
}
