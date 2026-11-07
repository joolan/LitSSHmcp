namespace LitSSHmcp.Core.Models;

public class PetConfig
{
    public bool Enabled { get; set; }
    public string AssetId { get; set; } = "builtin";
    public double Scale { get; set; } = 1.0;
    public double Opacity { get; set; } = 1.0;
    public int Speed { get; set; } = 70;
    public bool MovementEnabled { get; set; } = true;
    public bool TopMost { get; set; } = true;
    public bool ReduceMotion { get; set; }
}
