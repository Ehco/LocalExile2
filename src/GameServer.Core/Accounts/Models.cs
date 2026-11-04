namespace GameServer.Core.Accounts;

/// <param name="ClientDigestHash">PBKDF2 over the official client's password digest (SHA-256 of the UTF-16LE password),
/// so logins from the real client can be checked; null for accounts made before it existed until their next dev login.</param>
public sealed record Account(Guid Id, string Name, string PasswordHash, string PasswordSalt, DateTimeOffset CreatedUtc,
    string? ClientDigestHash = null);

/// <summary>PoE1's classes are Characters.dat IntegerIds (0 to 6); PoE2's own six classes follow them (Poe2Classes maps
/// both to PoE2's Characters.datc64 rows).</summary>
public enum CharacterClass { Scion, Marauder, Ranger, Witch, Duelist, Templar, Shadow, Warrior, Sorceress, Huntress, Mercenary, Monk, Druid }

/// <param name="AreaId">The dev JSON protocol's current area (GameSession); the official login flow does not use it.</param>
/// <param name="LastTown">WorldAreas Id of the last town the character entered (Auth records it when the Gateway resolves
/// a town ticket). Every official login starts there, as in the real game; null = never entered a town, which logs in at
/// <see cref="AccountService.StartingArea"/>.</param>
/// <param name="Portrait">The worn portrait (a MicrotransactionCharacterPortraitVariations variation, 0 = the class's):
/// the character list shows it (the Gateway reports picks with "set_portrait").</param>
/// <param name="QuestFlags">PoE2: the character's quest flags (QuestFlags.datc64 Ids), kept by the Gateway with
/// "set_quest_flags" (PORTING-POE2.md, wave 27). PoE1 keeps its own in the character saves.</param>
/// <param name="Waypoints">PoE2: the WorldAreas Ids whose waypoint the character has activated ("set_waypoints").</param>
/// <param name="Passives">PoE2: the allocated passive nodes (PassiveSkillGraphIds, "set_passives"; wave 28).</param>
public sealed record Character(
    Guid Id, Guid AccountId, string Name, CharacterClass Class, int Level,
    string League, string AreaId, DateTimeOffset CreatedUtc, DateTimeOffset LastPlayedUtc, string? LastTown = null,
    ushort Portrait = 0, HardcoreDeathRecord? HardcoreDeath = null, IReadOnlyList<string>? QuestFlags = null,
    IReadOnlyList<string>? Waypoints = null, IReadOnlyList<int>? Passives = null);

/// <summary>A committed Hardcore death. The source league is retained after migration; Ruthless deaths are terminal.
/// Stored with the account's character record, separately from mutable inventory/progress saves.</summary>
public sealed record HardcoreDeathRecord(string SourceLeague, DateTimeOffset AtUtc, bool PermanentlyDead);

public sealed record Session(string Token, Guid AccountId, DateTimeOffset IssuedUtc, DateTimeOffset ExpiresUtc);
