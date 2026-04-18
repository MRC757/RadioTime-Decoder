namespace WwvDecoder.Decoder;

/// <summary>
/// Represents the state of one bit position in the current 60-bit frame,
/// used to drive the per-bit visualization grid in the UI.
/// </summary>
public enum FrameCellState
{
    /// <summary>Position not yet reached in this frame.</summary>
    Empty,

    /// <summary>Received with high confidence (classifiers agreed, structurally valid).</summary>
    Confident,

    /// <summary>Received but erased (classifiers disagreed or structural mismatch).</summary>
    Erased,

    /// <summary>Estimated from wall-clock timing during a signal gap.</summary>
    GapFilled,

    /// <summary>Structurally corrected (e.g. spurious Marker at a data position → 0).</summary>
    Corrected,
}

/// <summary>
/// One cell in the 60-position frame visualization grid.
/// </summary>
public readonly struct FrameCell(int value, FrameCellState state)
{
    /// <summary>Bit value: 0 = Zero, 1 = One, 2 = Marker.</summary>
    public int Value { get; } = value;

    /// <summary>How this position was determined.</summary>
    public FrameCellState State { get; } = state;
}
