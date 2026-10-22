using SmartX.Shared;

namespace SmartX.Tests;

/// <summary>
/// The AVL tree on its own: balance, the four rotations, and range queries checked against a sorted list.
/// </summary>
/// <remarks>
/// Every randomised test is seeded, so a failure is the same failure on the next run, and none of them reads the clock.
/// </remarks>
public class AvlTreeTests
{
    private static AvlTree<int, string> TreeOf(params int[] keys)
    {
        var tree = new AvlTree<int, string>();

        foreach (var key in keys)
        {
            tree.Insert(key, $"v{key}");
            tree.ValidateInvariants();
        }

        return tree;
    }

    private static int[] Keys(IEnumerable<KeyValuePair<int, string>> entries) => [.. entries.Select(entry => entry.Key)];

    // ---- balance ---------------------------------------------------------------------------------------------

    [Fact]
    public void Random_inserts_and_removes_keep_every_invariant_after_every_operation()
    {
        var random = new Random(18);
        var tree = new AvlTree<int, string>();
        var reference = new SortedSet<int>();

        for (var step = 0; step < 3000; step++)
        {
            var key = random.Next(0, 400);

            if (random.Next(3) == 0)
            {
                Assert.Equal(reference.Remove(key), tree.Remove(key));
            }
            else
            {
                Assert.Equal(reference.Add(key), tree.Insert(key, $"v{key}"));
            }

            tree.ValidateInvariants();
            Assert.Equal(reference.Count, tree.Count);
        }

        Assert.Equal(reference, Keys(tree.InOrder()));
    }

    [Fact]
    public void Keys_inserted_in_ascending_order_stay_balanced_and_do_not_become_a_list()
    {
        var tree = new AvlTree<int, string>();

        // The case that ruins a plain binary search tree, and the shape timestamps arrive in.
        for (var key = 1; key <= 10_000; key++)
        {
            tree.Insert(key, "x");
        }

        tree.ValidateInvariants();

        // A perfectly balanced tree of 10,000 keys is 14 levels. AVL may be up to ~1.44 times that, never 10,000.
        Assert.InRange(tree.Height, 14, 20);
    }

    [Fact]
    public void Removing_every_key_leaves_an_empty_valid_tree()
    {
        var keys = Enumerable.Range(0, 500).ToArray();
        var tree = TreeOf(keys);

        foreach (var key in keys.OrderBy(key => (key * 7919) % 503))
        {
            Assert.True(tree.Remove(key));
            tree.ValidateInvariants();
        }

        Assert.Equal(0, tree.Count);
        Assert.Equal(0, tree.Height);
    }

    [Fact]
    public void A_duplicate_key_is_refused_and_does_not_replace_the_value()
    {
        var tree = new AvlTree<int, string>();

        Assert.True(tree.Insert(5, "first"));
        Assert.False(tree.Insert(5, "second"));

        Assert.Equal(1, tree.Count);
        Assert.True(tree.TryGetValue(5, out var value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void Removing_a_missing_key_changes_nothing()
    {
        var tree = TreeOf(1, 2, 3);

        Assert.False(tree.Remove(99));
        Assert.Equal(3, tree.Count);
    }

    [Fact]
    public void TryGetValue_finds_present_keys_and_reports_absent_ones()
    {
        var tree = TreeOf(10, 20, 30);

        Assert.True(tree.TryGetValue(20, out var found));
        Assert.Equal("v20", found);
        Assert.False(tree.TryGetValue(25, out _));
    }

    // ---- rotations -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LL", new[] { 3, 2, 1 })]
    [InlineData("RR", new[] { 1, 2, 3 })]
    [InlineData("LR", new[] { 3, 1, 2 })]
    [InlineData("RL", new[] { 1, 3, 2 })]
    public void Each_of_the_four_imbalances_is_fixed_to_the_same_balanced_shape(string imbalance, int[] insertOrder)
    {
        var tree = TreeOf(insertOrder);

        // Three keys in any order that tips the tree must come out with 2 on top and one child either side. If the
        // wrong rotation ran, the root would be 1 or 3, or the height would be 3.
        Assert.True(tree.TryGetRootKey(out var root), imbalance);
        Assert.Equal(2, root);
        Assert.Equal(2, tree.Height);
        Assert.Equal([1, 2, 3], Keys(tree.InOrder()));
    }

    [Fact]
    public void A_double_rotation_deeper_in_the_tree_rebalances_the_subtree_and_not_the_root()
    {
        // 50 is the root; inserting 5 after 10 under 20 makes the 20-10-5 side an LL case below the root.
        var tree = TreeOf(50, 20, 80, 10, 5);

        Assert.True(tree.TryGetRootKey(out var root));
        Assert.Equal(50, root);
        Assert.Equal(3, tree.Height);
    }

    // ---- range -----------------------------------------------------------------------------------------------

    [Fact]
    public void Range_matches_a_sorted_list_filtered_the_slow_way_for_random_windows()
    {
        var random = new Random(180);
        var keys = Enumerable.Range(0, 800).Select(_ => random.Next(0, 5000)).Distinct().ToList();
        var tree = new AvlTree<int, string>();

        foreach (var key in keys)
        {
            tree.Insert(key, "x");
        }

        var sorted = keys.Order().ToList();

        for (var query = 0; query < 500; query++)
        {
            var from = random.Next(-100, 5100);
            var to = random.Next(-100, 5100);

            var expected = sorted.Where(key => key >= from && key <= to).ToArray();

            Assert.Equal(expected, Keys(tree.Range(from, to)));
        }
    }

    [Fact]
    public void Range_edge_cases_are_empty_single_inclusive_or_clipped()
    {
        var tree = TreeOf(10, 20, 30, 40, 50);

        Assert.Empty(tree.Range(21, 29));                       // between two keys
        Assert.Equal([30], Keys(tree.Range(30, 30)));           // one key, both ends inclusive
        Assert.Equal([20, 30, 40], Keys(tree.Range(20, 40)));   // both ends are included
        Assert.Equal([10, 20, 30, 40, 50], Keys(tree.Range(-1000, 1000)));   // past both ends
        Assert.Empty(tree.Range(100, 200));                     // wholly after
        Assert.Empty(tree.Range(-200, -100));                   // wholly before
        Assert.Empty(tree.Range(40, 20));                       // from after to
        Assert.Empty(new AvlTree<int, string>().Range(0, 100)); // nothing stored
    }

    [Fact]
    public void From_starts_at_the_first_key_at_or_after_and_is_lazy()
    {
        var tree = TreeOf(Enumerable.Range(1, 1000).ToArray());

        Assert.Equal([500, 501, 502], Keys(tree.From(500).Take(3)));
        Assert.Equal([700], Keys(tree.From(699).Skip(1).Take(1)));
        Assert.Empty(tree.From(1001));
    }

    [Fact]
    public void Reverse_order_is_the_exact_opposite_of_in_order()
    {
        var tree = TreeOf(7, 3, 9, 1, 5, 8, 10);

        Assert.Equal(Keys(tree.InOrder()).Reverse(), Keys(tree.InReverseOrder()));
    }

    // ---- Min, Max, Floor, Ceiling ---------------------------------------------------------------------------

    [Fact]
    public void Min_and_Max_of_an_empty_tree_are_null_and_otherwise_the_ends()
    {
        var empty = new AvlTree<int, string>();

        Assert.Null(empty.Min());
        Assert.Null(empty.Max());

        var tree = TreeOf(40, 10, 30, 20);

        Assert.Equal(10, tree.Min()!.Value.Key);
        Assert.Equal("v40", tree.Max()!.Value.Value);
    }

    [Fact]
    public void Floor_and_Ceiling_find_the_neighbours_of_a_key()
    {
        var tree = TreeOf(10, 20, 30);

        Assert.Equal(20, tree.Floor(25)!.Value.Key);
        Assert.Equal(30, tree.Ceiling(25)!.Value.Key);

        // An exact hit is its own floor and ceiling.
        Assert.Equal(20, tree.Floor(20)!.Value.Key);
        Assert.Equal(20, tree.Ceiling(20)!.Value.Key);

        // Off either end.
        Assert.Null(tree.Floor(9));
        Assert.Equal(10, tree.Ceiling(9)!.Value.Key);
        Assert.Equal(30, tree.Floor(31)!.Value.Key);
        Assert.Null(tree.Ceiling(31));

        Assert.Null(new AvlTree<int, string>().Floor(1));
        Assert.Null(new AvlTree<int, string>().Ceiling(1));
    }

    [Fact]
    public void Floor_and_Ceiling_match_a_sorted_list_for_random_probes()
    {
        var random = new Random(181);
        var keys = Enumerable.Range(0, 300).Select(_ => random.Next(0, 2000)).Distinct().Order().ToList();
        var tree = new AvlTree<int, string>();

        foreach (var key in keys)
        {
            tree.Insert(key, "x");
        }

        for (var probe = -5; probe < 2010; probe += 3)
        {
            var floor = keys.Where(key => key <= probe).Select(key => (int?)key).LastOrDefault();
            var ceiling = keys.Where(key => key >= probe).Select(key => (int?)key).FirstOrDefault();

            Assert.Equal(floor, tree.Floor(probe)?.Key);
            Assert.Equal(ceiling, tree.Ceiling(probe)?.Key);
        }
    }

    // ---- the checker itself ----------------------------------------------------------------------------------

    [Fact]
    public void A_struct_key_is_compared_through_the_generic_interface()
    {
        // A value-type key is the history store's case. This only needs to run: IComparable<T> on a struct is
        // dispatched without boxing, which the constraint guarantees at compile time.
        var tree = new AvlTree<(int Major, int Minor), int>();
        tree.Insert((1, 2), 12);
        tree.Insert((1, 1), 11);

        Assert.Equal([11, 12], tree.InOrder().Select(entry => entry.Value));
    }
}
