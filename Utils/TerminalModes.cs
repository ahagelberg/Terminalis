namespace Terminalis.Utils;

public class TerminalModes
{
    public bool InAlternateScreen { get; set; } = false;
    public bool CursorKeyMode { get; set; } = false;
    public bool InsertMode { get; set; } = false;
    public bool CursorVisible { get; set; } = true;
    public bool BracketedPasteMode { get; set; } = false;
    public int ScrollRegionTop { get; set; } = -1;
    public int ScrollRegionBottom { get; set; } = -1;

    public bool IsScrollRegionActive => ScrollRegionTop >= 0 && ScrollRegionBottom >= ScrollRegionTop;

    public void ResetScrollRegion()
    {
        ScrollRegionTop = -1;
        ScrollRegionBottom = -1;
    }

    public void SetScrollRegion(int top, int bottom)
    {
        ScrollRegionTop = top;
        ScrollRegionBottom = bottom;
    }
}
