using LitSSHmcp.App.Controls;
using Xunit;

namespace LitSSHmcp.App.Tests;

public class ChatScrollPolicyTests
{
    // 视口 600，内容 3000，底部偏移应为 2400。
    private const double Viewport = 600;
    private const double Extent = 3000;

    [Fact]
    public void At_bottom_allows_subpixel_slack()
    {
        Assert.True(ChatScrollPolicy.IsAtBottom(2400, Viewport, Extent));
        Assert.True(ChatScrollPolicy.IsAtBottom(2399, Viewport, Extent));
        Assert.False(ChatScrollPolicy.IsAtBottom(2300, Viewport, Extent));
    }

    [Fact]
    public void Content_that_fits_viewport_is_treated_as_bottom()
    {
        Assert.True(ChatScrollPolicy.IsAtBottom(0, 600, 400));
    }

    [Fact]
    public void Following_stays_when_content_grew_and_viewport_moved_above_bottom()
    {
        // 跟着底部时内容突然变高 200px：偏移还没跟上，但仍应继续跟随。
        Assert.True(ChatScrollPolicy.Resolve(
            following: true, verticalChange: 0,
            verticalOffset: 2400, viewportHeight: Viewport, extentHeight: 3200));
    }

    [Fact]
    public void Paused_stays_paused_when_content_keeps_growing()
    {
        // 用户已经向上翻历史：任务继续输出（内容变高）也不能把视图拉回底部。
        Assert.False(ChatScrollPolicy.Resolve(
            following: false, verticalChange: 0,
            verticalOffset: 1200, viewportHeight: Viewport, extentHeight: 3200));
    }

    [Fact]
    public void Scrolling_up_pauses_following_even_before_reaching_history_top()
    {
        Assert.False(ChatScrollPolicy.Resolve(
            following: true, verticalChange: -48,
            verticalOffset: 2352, viewportHeight: Viewport, extentHeight: Extent));
    }

    [Fact]
    public void Scrollbar_stop_near_but_not_at_bottom_does_not_resume()
    {
        Assert.False(ChatScrollPolicy.Resolve(
            following: false, verticalChange: -10,
            verticalOffset: 2380, viewportHeight: Viewport, extentHeight: Extent));
    }

    [Fact]
    public void Scrolling_down_but_not_at_bottom_stays_paused()
    {
        Assert.False(ChatScrollPolicy.Resolve(
            following: false, verticalChange: 48,
            verticalOffset: 2000, viewportHeight: Viewport, extentHeight: Extent));
    }

    [Fact]
    public void Reaching_the_real_bottom_resumes_following()
    {
        Assert.True(ChatScrollPolicy.Resolve(
            following: false, verticalChange: 400,
            verticalOffset: 2400, viewportHeight: Viewport, extentHeight: Extent));
    }

    [Fact]
    public void Up_scroll_wins_even_when_extent_changed_in_the_same_event()
    {
        // 流式输出时“内容变高 + 用户上滚”可能落在同一个 ScrollChanged 里，用户意图优先。
        Assert.False(ChatScrollPolicy.Resolve(
            following: true, verticalChange: -48,
            verticalOffset: 2352, viewportHeight: Viewport, extentHeight: 3200));
    }
}
