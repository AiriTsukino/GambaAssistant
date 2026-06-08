using GambaAssistant.Models.Players;

namespace GambaAssistant.Games.DeathRoll;

public enum DeathRollTournamentStatus
{
    Setup,
    Running,
    Complete
}

public enum DeathRollMatchStatus
{
    Waiting,
    SeedingRolls,
    Playing,
    Complete,
    Bye
}

[Serializable]
public sealed class DeathRollPlayer
{
    public PlayerIdentity Identity { get; set; }
    public string DisplayName => Identity.ToString();
    public bool Eliminated { get; set; }
}

[Serializable]
public sealed class DeathRollMatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int RoundIndex { get; set; }
    public int MatchIndex { get; set; }
    public DeathRollPlayer? PlayerA { get; set; }
    public DeathRollPlayer? PlayerB { get; set; }
    public DeathRollPlayer? Winner { get; set; }
    public DeathRollPlayer? Loser { get; set; }
    public DeathRollPlayer? CurrentTurn { get; set; }
    public DeathRollMatchStatus Status { get; set; } = DeathRollMatchStatus.Waiting;
    public int CurrentMax { get; set; } = 999;
    public int? SeedRollA { get; set; }
    public int? SeedRollB { get; set; }
    public bool FirstDeathRollTaken { get; set; }
    public DateTime OpeningRollsAcceptedAfterUtc { get; set; } = DateTime.MinValue;
    public List<string> History { get; set; } = new();
    public string Label => $"R{RoundIndex + 1} Match {MatchIndex + 1}";

    public bool HasPlayer(string displayName)
        => IsPlayer(PlayerA, displayName) || IsPlayer(PlayerB, displayName);

    public DeathRollPlayer? GetPlayer(string displayName)
    {
        if (IsPlayer(PlayerA, displayName)) return PlayerA;
        if (IsPlayer(PlayerB, displayName)) return PlayerB;
        return null;
    }

    public DeathRollPlayer? OtherPlayer(DeathRollPlayer player)
    {
        if (PlayerA is not null && string.Equals(PlayerA.DisplayName, player.DisplayName, StringComparison.OrdinalIgnoreCase)) return PlayerB;
        if (PlayerB is not null && string.Equals(PlayerB.DisplayName, player.DisplayName, StringComparison.OrdinalIgnoreCase)) return PlayerA;
        return null;
    }

    private static bool IsPlayer(DeathRollPlayer? player, string displayName)
    {
        if (player is null || string.IsNullOrWhiteSpace(displayName)) return false;
        var target = Normalize(displayName);
        var playerName = Normalize(player.DisplayName);
        var shortName = Normalize(player.Identity.Name);
        return playerName == target || shortName == target || playerName.Contains(target) || target.Contains(shortName);
    }

    private static string Normalize(string value)
        => new string(value.Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) || c == '@').ToArray()).Trim().ToLowerInvariant();
}

[Serializable]
public sealed class DeathRollTournament
{
    public DeathRollTournamentStatus Status { get; set; } = DeathRollTournamentStatus.Setup;
    public List<DeathRollPlayer> Entrants { get; set; } = new();
    public List<List<DeathRollMatch>> Rounds { get; set; } = new();
    public Guid? ActiveMatchId { get; set; }
    public int StartingMax { get; set; } = 999;
    public int SeedingMax { get; set; } = 10;

    public IEnumerable<DeathRollMatch> AllMatches => Rounds.SelectMany(r => r);
    public DeathRollMatch? ActiveMatch => ActiveMatchId.HasValue ? AllMatches.FirstOrDefault(m => m.Id == ActiveMatchId.Value) : null;
}
