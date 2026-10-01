using SearchService.Domain;

namespace SearchService.IntegrationTests;

public class ReciprocalRankFusionTests
{
    private static string[] List(params string[] keys) => keys;

    [Fact]
    public void An_item_both_lists_rank_in_the_middle_beats_one_only_a_single_list_ranks_first()
    {
        var keyword = List("a", "shared", "c");
        var semantic = List("x", "shared", "z");

        var fused = ReciprocalRankFusion.Fuse([keyword, semantic]);

        Assert.Equal("shared", fused[0].Key);
    }

    [Fact]
    public void The_score_is_the_sum_of_one_over_k_plus_rank_for_each_list_the_item_is_in()
    {
        var fused = ReciprocalRankFusion.Fuse([List("a", "b"), List("b")], k: 60);

        Assert.Equal(1d / 62 + 1d / 61, fused.Single(f => f.Key == "b").Score, precision: 12);
        Assert.Equal(1d / 61, fused.Single(f => f.Key == "a").Score, precision: 12);
    }

    [Fact]
    public void Equal_scores_are_ordered_by_the_better_rank_then_by_key_so_the_order_is_stable()
    {
        // "a" (rank 1 in the first list) and "x" (rank 1 in the second) tie; "b" and "y" tie one place lower.
        var fused = ReciprocalRankFusion.Fuse([List("a", "b"), List("x", "y")]);

        Assert.Equal(["a", "x", "b", "y"], fused.Select(f => f.Key).ToArray());
    }

    [Fact]
    public void A_key_repeated_within_one_list_counts_once_at_its_first_rank()
    {
        var fused = ReciprocalRankFusion.Fuse([List("a", "a", "a")], k: 60);

        Assert.Equal(1d / 61, Assert.Single(fused).Score, precision: 12);
    }

    [Fact]
    public void No_lists_or_empty_lists_fuse_to_nothing()
    {
        Assert.Empty(ReciprocalRankFusion.Fuse([]));
        Assert.Empty(ReciprocalRankFusion.Fuse([List(), List()]));
    }

    [Fact]
    public void One_list_keeps_its_own_order()
    {
        var fused = ReciprocalRankFusion.Fuse([List("c", "a", "b")]);

        Assert.Equal(["c", "a", "b"], fused.Select(f => f.Key).ToArray());
    }
}
