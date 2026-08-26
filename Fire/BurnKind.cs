namespace FireFront.Fire
{
    /// <summary>What kind of vanilla object a burning Component actually is.
    /// Needed because Piece/Tree/Log have different field names and no shared
    /// destroy signature — ValheimBridge branches on this to do the right thing.</summary>
    public enum BurnKind
    {
        Unknown,
        Piece,  // WearNTear - player-built structures
        Tree,   // TreeBase - standing trees
        Log     // TreeLog - felled logs
    }
}
