/// <summary>官方多人世界的地图定义：小镇、可建房的空地、出生点。</summary>
public sealed record MultiplayerPlotDef(string Id, string Name, int X, int Y, int W, int H);

public sealed record MultiplayerSpawnDef(string Id, string Name, int X, int Y);

public sealed record MultiplayerMapDef(
    string Id, string Name, int Width, int Height, int TownX, int TownY, string TownName,
    IReadOnlyList<MultiplayerPlotDef> Plots, IReadOnlyList<MultiplayerSpawnDef> Spawns);

// Cell coordinates supplied by the authoritative world, not the overview UI.
public sealed record OfficialWorldPlot(string Id, int X, int Y, int W, int H);
public sealed record OfficialWorldSpawn(string Id, int X, int Y);
public sealed record OfficialWorldMap(int Version, string WorldId, long Generation, int Seed, int TerrainVersion,
    double BiomeScale, int TownX, int TownY, int HomeX, int HomeY,
    OfficialWorldPlot[] Plots, OfficialWorldSpawn[] Spawns)
{
    private static bool Cell(int x, int y) => x is >= -4096 and < 4096 && y is >= -2048 and < 2048;
    private static bool Overlaps(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh)
        => ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah;

    public bool Valid()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(WorldId) || WorldId.Length > 64 || WorldId.Any(char.IsControl)
            || Generation is <= 0 or > 9007199254740991L || Seed == 0 || TerrainVersion <= 0
            || !double.IsFinite(BiomeScale) || BiomeScale <= 0 || BiomeScale > 100
            || !Cell(TownX, TownY) || !Cell(HomeX, HomeY) || Plots is not { Length: 8 } || Spawns is not { Length: 4 }) return false;
        var plotIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var plot in Plots)
        {
            if (plot == null || !Enumerable.Range(1, 8).Any(i => plot.Id == "p" + i) || !plotIds.Add(plot.Id)
                || plot.W != 32 || plot.H != 32 || !Cell(plot.X, plot.Y)
                || plot.X + plot.W > 4096 || plot.Y + plot.H > 2048) return false;
        }
        for (int i = 0; i < Plots.Length; i++)
        for (int j = i + 1; j < Plots.Length; j++)
            if (Overlaps(Plots[i].X - 24, Plots[i].Y - 24, 80, 80,
                Plots[j].X, Plots[j].Y, Plots[j].W, Plots[j].H)) return false;
        var spawnIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spawn in Spawns)
        {
            if (spawn == null || spawn.Id is not ("s_town" or "s_east" or "s_west" or "s_north") || !spawnIds.Add(spawn.Id)
                || spawn.X <= -4096 || spawn.X >= 4095 || spawn.Y <= -2048 || spawn.Y >= 2047
                || Plots.Any(p => Overlaps(p.X, p.Y, p.W, p.H, spawn.X - 1, spawn.Y - 1, 3, 3))) return false;
        }
        return true;
    }

    public OfficialWorldMap Canonical() => this with {
        Plots = Plots.OrderBy(p => p.Id, StringComparer.Ordinal).ToArray(),
        Spawns = Spawns.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray()
    };
}

public static class MultiplayerMaps
{
    public const string TownId = "town";

    /// <summary>首张官方地图：小镇在中间，八块空地在周围，四组出生点。</summary>
    public static MultiplayerMapDef Town { get; } = new(
        TownId, "穗吟镇周边", 960, 540, 480, 270, "穗吟镇",
        new[]
        {
            new MultiplayerPlotDef("p1", "东田", 700, 150, 100, 80),
            new MultiplayerPlotDef("p2", "西田", 160, 150, 100, 80),
            new MultiplayerPlotDef("p3", "河边", 730, 340, 110, 80),
            new MultiplayerPlotDef("p4", "林边", 120, 340, 100, 80),
            new MultiplayerPlotDef("p5", "北坡", 430, 70, 100, 70),
            new MultiplayerPlotDef("p6", "磨坊旁", 300, 400, 100, 70),
            new MultiplayerPlotDef("p7", "南岸", 560, 410, 100, 70),
            new MultiplayerPlotDef("p8", "山脚", 300, 80, 100, 70),
        },
        new[]
        {
            new MultiplayerSpawnDef("s_town", "小镇广场", 480, 310),
            new MultiplayerSpawnDef("s_east", "东门", 760, 260),
            new MultiplayerSpawnDef("s_west", "西门", 200, 260),
            new MultiplayerSpawnDef("s_north", "北路口", 480, 170),
        });

    public static MultiplayerMapDef? Get(string id)
        => string.Equals(id, Town.Id, StringComparison.Ordinal) ? Town : null;
}
