namespace SmartX.Shared;

/// <summary>
/// A self-balancing binary search tree (AVL), written from scratch: no <c>SortedDictionary</c>, <c>SortedSet</c> or
/// library tree is used inside it.
/// </summary>
/// <typeparam name="TKey">The sort key. Constrained to <see cref="IComparable{T}"/> so comparing two keys is a direct call.</typeparam>
/// <typeparam name="TValue">What is stored under a key.</typeparam>
/// <remarks>
/// <b>Why a tree and why AVL.</b> History is read by time range ("14:00 to 16:00 for Node A"). A tree keeps keys in order, so
/// a range is found by <i>descending</i> to its start, which costs one comparison per level, instead of scanning from the
/// oldest entry. A plain binary search tree degrades to a linked list when keys arrive in order, and timestamps do, so the
/// tree must balance itself. AVL keeps the heights of every node's two subtrees within one of each other, which bounds the
/// height at about 1.44 log2 n and keeps every operation O(log n). It balances harder than a red-black tree, so lookups are
/// slightly faster and inserts do slightly more rotating; history is read far more than it is rewritten, which is the
/// reason for choosing it.
/// <para>
/// <b>Generic constraint, not <c>object</c>.</b> <c>where TKey : IComparable&lt;TKey&gt;</c> means a struct key (the history
/// key is one) is compared through the generic interface, never boxed.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> The owner locks. A tree that locked itself would still need the owner to lock across a
/// read-then-write, so the lock lives where the compound operations are.
/// </para>
/// <para>
/// Recursion depth is the tree height, at most about 30 for a million keys, so recursive insert and remove cannot
/// overflow the stack. Enumeration is iterative with an explicit stack of the same size.
/// </para>
/// </remarks>
public sealed class AvlTree<TKey, TValue>
    where TKey : IComparable<TKey>
{
    private Node? _root;

    /// <summary>How many keys the tree holds.</summary>
    public int Count { get; private set; }

    /// <summary>The number of levels. An empty tree is 0 and a single key is 1.</summary>
    /// <remarks>Read from the root's stored height, so it is O(1).</remarks>
    public int Height => HeightOf(_root);

    /// <summary>
    /// Adds a key. Returns <see langword="false"/> and leaves the tree unchanged when the key is already present.
    /// </summary>
    /// <remarks>
    /// Never replaces. The history store relies on this: a repeated (timestamp, sequence) is a caller fault, and silently
    /// overwriting a record would be worse than refusing it.
    /// </remarks>
    public bool Insert(TKey key, TValue value)
    {
        var added = false;
        _root = Insert(_root, key, value, ref added);

        if (added)
        {
            Count++;
        }

        return added;
    }

    /// <summary>Removes a key. Returns <see langword="false"/> when it was not present.</summary>
    public bool Remove(TKey key)
    {
        var removed = false;
        _root = Remove(_root, key, ref removed);

        if (removed)
        {
            Count--;
        }

        return removed;
    }

    /// <summary>Finds the value stored under a key.</summary>
    public bool TryGetValue(TKey key, out TValue value)
    {
        var node = _root;

        while (node is not null)
        {
            var order = key.CompareTo(node.Key);

            if (order == 0)
            {
                value = node.Value;

                return true;
            }

            node = order < 0 ? node.Left : node.Right;
        }

        value = default!;

        return false;
    }

    /// <summary>The key at the root, which the rotation tests use to see which way the tree turned.</summary>
    public bool TryGetRootKey(out TKey key)
    {
        if (_root is null)
        {
            key = default!;

            return false;
        }

        key = _root.Key;

        return true;
    }

    /// <summary>The smallest key, or <see langword="null"/> when the tree is empty. O(log n): follow the left edge.</summary>
    public KeyValuePair<TKey, TValue>? Min()
    {
        if (_root is null)
        {
            return null;
        }

        var node = _root;

        while (node.Left is not null)
        {
            node = node.Left;
        }

        return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
    }

    /// <summary>The largest key, or <see langword="null"/> when the tree is empty. O(log n): follow the right edge.</summary>
    public KeyValuePair<TKey, TValue>? Max()
    {
        if (_root is null)
        {
            return null;
        }

        var node = _root;

        while (node.Right is not null)
        {
            node = node.Right;
        }

        return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
    }

    /// <summary>
    /// The largest key that is less than or equal to <paramref name="key"/>, or <see langword="null"/> when every key is
    /// greater. "The latest reading at or before 14:00."
    /// </summary>
    public KeyValuePair<TKey, TValue>? Floor(TKey key)
    {
        Node? best = null;
        var node = _root;

        while (node is not null)
        {
            var order = key.CompareTo(node.Key);

            if (order == 0)
            {
                return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
            }

            if (order < 0)
            {
                node = node.Left;
            }
            else
            {
                // This node is a candidate, and anything better is to its right.
                best = node;
                node = node.Right;
            }
        }

        return best is null ? null : new KeyValuePair<TKey, TValue>(best.Key, best.Value);
    }

    /// <summary>
    /// The smallest key that is greater than or equal to <paramref name="key"/>, or <see langword="null"/> when every key
    /// is smaller. "The first reading at or after 14:00."
    /// </summary>
    public KeyValuePair<TKey, TValue>? Ceiling(TKey key)
    {
        Node? best = null;
        var node = _root;

        while (node is not null)
        {
            var order = key.CompareTo(node.Key);

            if (order == 0)
            {
                return new KeyValuePair<TKey, TValue>(node.Key, node.Value);
            }

            if (order > 0)
            {
                node = node.Right;
            }
            else
            {
                // This node is a candidate, and anything better is to its left.
                best = node;
                node = node.Left;
            }
        }

        return best is null ? null : new KeyValuePair<TKey, TValue>(best.Key, best.Value);
    }

    /// <summary>Every entry in ascending key order.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InOrder() => EnumerateFrom(default!, bounded: false);

    /// <summary>Every entry in descending key order. The newest-first walk behind "the latest N readings".</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> InReverseOrder()
    {
        var path = new Stack<Node>();

        for (var node = _root; node is not null; node = node.Right)
        {
            path.Push(node);
        }

        while (path.Count > 0)
        {
            var node = path.Pop();

            yield return new KeyValuePair<TKey, TValue>(node.Key, node.Value);

            for (var next = node.Left; next is not null; next = next.Right)
            {
                path.Push(next);
            }
        }
    }

    /// <summary>
    /// Every entry with a key greater than or equal to <paramref name="from"/>, in ascending order, produced lazily.
    /// </summary>
    /// <remarks>
    /// The seek is O(log n); each entry after it is amortised O(1), and a caller that stops early pays only for what it
    /// took. The store uses this for a half-open range, stopping at the first key at or past the end.
    /// </remarks>
    public IEnumerable<KeyValuePair<TKey, TValue>> From(TKey from) => EnumerateFrom(from, bounded: true);

    /// <summary>
    /// Every entry with <paramref name="from"/> &lt;= key &lt;= <paramref name="to"/>, in ascending order.
    /// An empty list when <paramref name="from"/> is greater than <paramref name="to"/>.
    /// </summary>
    /// <remarks>
    /// <b>O(log n + k)</b>, where k is the number of results. The descent to the first key at or after <c>from</c> costs one
    /// comparison per level, which is log n; the in-order walk from there visits exactly the k results and stops at the first
    /// key past <c>to</c>. A scan of a list or a <c>SortedDictionary</c> has to walk past every key before <c>from</c> first,
    /// so it costs O(n + k): the same answer, but the price grows with how much history exists and not with how much was
    /// asked for.
    /// </remarks>
    public List<KeyValuePair<TKey, TValue>> Range(TKey from, TKey to)
    {
        var found = new List<KeyValuePair<TKey, TValue>>();

        if (from.CompareTo(to) > 0)
        {
            return found;
        }

        foreach (var entry in EnumerateFrom(from, bounded: true))
        {
            if (entry.Key.CompareTo(to) > 0)
            {
                break;
            }

            found.Add(entry);
        }

        return found;
    }

    /// <summary>
    /// Checks every rule that makes this an AVL tree and throws if one is broken.
    /// </summary>
    /// <remarks>
    /// For tests and debugging: it visits every node, so it is O(n) and nothing on a hot path calls it. It is an ordinary
    /// method and not a <c>[Conditional("DEBUG")]</c> one so a test run in Release still checks something. Verifies that
    /// each stored height equals the real height, that every balance factor is -1, 0 or 1, that keys ascend strictly in
    /// order, and that the node count matches <see cref="Count"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">An invariant does not hold. The message says which.</exception>
    public void ValidateInvariants()
    {
        var nodes = 0;
        Check(_root, ref nodes);

        if (nodes != Count)
        {
            throw new InvalidOperationException($"Count is {Count} but the tree holds {nodes} nodes.");
        }

        TKey? previous = default;
        var first = true;

        foreach (var entry in InOrder())
        {
            if (!first && previous!.CompareTo(entry.Key) >= 0)
            {
                throw new InvalidOperationException($"Keys are not strictly ascending: {previous} then {entry.Key}.");
            }

            previous = entry.Key;
            first = false;
        }
    }

    private static int Check(Node? node, ref int nodes)
    {
        if (node is null)
        {
            return 0;
        }

        nodes++;

        var left = Check(node.Left, ref nodes);
        var right = Check(node.Right, ref nodes);
        var height = 1 + Math.Max(left, right);

        if (node.Height != height)
        {
            throw new InvalidOperationException($"Node {node.Key} stores height {node.Height} but its real height is {height}.");
        }

        if (Math.Abs(left - right) > 1)
        {
            throw new InvalidOperationException($"Node {node.Key} is out of balance: left height {left}, right height {right}.");
        }

        return height;
    }

    /// <summary>
    /// Walks in order from the first key at or after <paramref name="from"/>, or from the smallest key when not <paramref name="bounded"/>.
    /// </summary>
    /// <remarks>
    /// <b>The seek.</b> The stack holds the ancestors the walk has yet to visit. Descending toward <c>from</c>, a node whose
    /// key is at or after it is pushed (it is a future result, visited after everything beneath it) and the descent goes
    /// left; a node whose key is before it, and its whole left subtree, are skipped and the descent goes right. That is
    /// the O(log n) step that a scan cannot do. After that, popping a node yields it and pushes the left spine of its right
    /// subtree, which is the next-larger keys in order.
    /// </remarks>
    private IEnumerable<KeyValuePair<TKey, TValue>> EnumerateFrom(TKey from, bool bounded)
    {
        var path = new Stack<Node>();
        var node = _root;

        while (node is not null)
        {
            if (!bounded || node.Key.CompareTo(from) >= 0)
            {
                path.Push(node);
                node = node.Left;
            }
            else
            {
                node = node.Right;
            }
        }

        while (path.Count > 0)
        {
            var next = path.Pop();

            yield return new KeyValuePair<TKey, TValue>(next.Key, next.Value);

            for (var left = next.Right; left is not null; left = left.Left)
            {
                path.Push(left);
            }
        }
    }

    private static Node Insert(Node? node, TKey key, TValue value, ref bool added)
    {
        if (node is null)
        {
            added = true;

            return new Node(key, value);
        }

        var order = key.CompareTo(node.Key);

        if (order < 0)
        {
            node.Left = Insert(node.Left, key, value, ref added);
        }
        else if (order > 0)
        {
            node.Right = Insert(node.Right, key, value, ref added);
        }
        else
        {
            // Already present. Nothing below changed, so nothing above needs rebalancing.
            return node;
        }

        return added ? Rebalance(node) : node;
    }

    private static Node? Remove(Node? node, TKey key, ref bool removed)
    {
        if (node is null)
        {
            return null;
        }

        var order = key.CompareTo(node.Key);

        if (order < 0)
        {
            node.Left = Remove(node.Left, key, ref removed);
        }
        else if (order > 0)
        {
            node.Right = Remove(node.Right, key, ref removed);
        }
        else
        {
            removed = true;

            // At most one child: the child takes this node's place.
            if (node.Left is null)
            {
                return node.Right;
            }

            if (node.Right is null)
            {
                return node.Left;
            }

            // Two children: this node is replaced by its in-order successor, the smallest key in the right subtree, which
            // is then removed from where it was. Taking the successor keeps the ordering without moving anything else.
            var successor = node.Right;

            while (successor.Left is not null)
            {
                successor = successor.Left;
            }

            var ignored = false;
            var right = Remove(node.Right, successor.Key, ref ignored);
            node = new Node(successor.Key, successor.Value) { Left = node.Left, Right = right };
        }

        return removed ? Rebalance(node) : node;
    }

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    /// <summary>Right height minus left height. Positive means right-heavy.</summary>
    private static int Balance(Node node) => HeightOf(node.Right) - HeightOf(node.Left);

    private static void UpdateHeight(Node node) => node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    /// <summary>
    /// Restores the balance at one node after something below it changed, and returns the node now at its position.
    /// </summary>
    /// <remarks>
    /// Called on the way back up from an insert or a remove, so every node whose subtree changed is visited once, deepest
    /// first. A balance of two means a subtree grew (or the other shrank) by too much, and which rotation fixes it depends
    /// on <i>where</i> the extra height is: on the outside (a single rotation) or the inside (a double one).
    /// </remarks>
    private static Node Rebalance(Node node)
    {
        UpdateHeight(node);
        var balance = Balance(node);

        if (balance < -1)
        {
            // Left-heavy. Outside-heavy (or even) left child: LL. Inside-heavy: LR.
            return Balance(node.Left!) <= 0 ? RotateLeftLeft(node) : RotateLeftRight(node);
        }

        if (balance > 1)
        {
            return Balance(node.Right!) >= 0 ? RotateRightRight(node) : RotateRightLeft(node);
        }

        return node;
    }

    /// <summary>
    /// LL case: the left child's left side is too tall. One rotation to the right lifts the left child above its parent.
    /// </summary>
    private static Node RotateLeftLeft(Node node)
    {
        var pivot = node.Left!;
        node.Left = pivot.Right;
        pivot.Right = node;

        // The lower node first: it is now the pivot's child, so its height feeds the pivot's.
        UpdateHeight(node);
        UpdateHeight(pivot);

        return pivot;
    }

    /// <summary>
    /// RR case: the right child's right side is too tall. One rotation to the left, the mirror of <see cref="RotateLeftLeft"/>.
    /// </summary>
    private static Node RotateRightRight(Node node)
    {
        var pivot = node.Right!;
        node.Right = pivot.Left;
        pivot.Left = node;

        UpdateHeight(node);
        UpdateHeight(pivot);

        return pivot;
    }

    /// <summary>
    /// LR case: the left child's <i>right</i> side is too tall, so a single rotation would only move the problem across.
    /// Rotate the left child left to turn it into an LL case, then rotate the node right.
    /// </summary>
    private static Node RotateLeftRight(Node node)
    {
        node.Left = RotateRightRight(node.Left!);

        return RotateLeftLeft(node);
    }

    /// <summary>
    /// RL case: the right child's <i>left</i> side is too tall. Rotate the right child right to make it an RR case, then
    /// rotate the node left.
    /// </summary>
    private static Node RotateRightLeft(Node node)
    {
        node.Right = RotateLeftLeft(node.Right!);

        return RotateRightRight(node);
    }

    private sealed class Node(TKey key, TValue value)
    {
        public TKey Key { get; } = key;

        public TValue Value { get; } = value;

        /// <summary>Stored, and not worked out when needed, so checking a balance is two reads and not a walk.</summary>
        public int Height { get; set; } = 1;

        public Node? Left { get; set; }

        public Node? Right { get; set; }
    }
}
