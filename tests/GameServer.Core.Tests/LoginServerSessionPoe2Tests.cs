using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Login;
using GameServer.Storage;

namespace GameServer.Core.Tests;

/// <summary>Auth's answers to the PoE2 Steam client (PORTING-POE2.md, wave 15).</summary>
public class LoginServerSessionPoe2Tests
{
    static readonly TokenLoginPoe2 SteamLogin = new(0, new byte[32], new byte[32], 0, new byte[159]);

    // Through the PoE2 codec both ways, as GameServer.Auth does it (minus encryption).
    static List<ILoginMessage> RoundTrip(LoginServerSession session, ILoginMessage request)
    {
        var up = new LoginMessageStream(LoginDirection.ClientToServer, LoginDialect.Poe2).Push(LoginMessage.Encode(request));
        var replies = session.Handle(Assert.Single(up));
        var down = new LoginMessageStream(LoginDirection.ServerToClient, LoginDialect.Poe2).Push([.. replies.SelectMany(LoginMessage.Encode)]);
        Assert.Equal(replies.Count, down.Count);
        return down;
    }

    [Fact]
    public void A_steam_login_opens_the_configured_account_and_creates_it_once()
    {
        var accounts = new AccountService(new JsonAccountStore());

        var first = new LoginServerSession(accounts) { SteamAccount = "steam" };
        var replies = RoundTrip(first, SteamLogin);
        var second = new LoginServerSession(accounts) { SteamAccount = "steam" };
        RoundTrip(second, SteamLogin);

        var result = Assert.IsType<LoginResultPoe2>(replies[0]);
        Assert.Equal(LoginServerSession.ResultOk, result.Result);
        Assert.Equal(LoginServerSession.Poe2LoginRecordId, result.U32_10);
        Assert.Equal(LoginServerSession.FlagVerified, result.Flags0);
        var leagues = Assert.IsType<LeagueListPoe2>(replies[1]).Leagues;
        // The create screen needs a league to select (0x12's sender throws without one); PoE2 has no Ruthless.
        Assert.Equal(new[] { "Hardcore", "SSF Hardcore", "SSF Standard", "Standard" }, leagues.Select(l => l.Name).Order());
        Assert.Equal(LeagueEntry.Main, Assert.Single(leagues, l => l.Name == "Standard").Flags0);
        Assert.Equal("steam", first.Account!.Name);
        Assert.Equal(first.Account.Id, second.Account!.Id);
    }

    [Fact]
    public void The_first_steam_login_gives_the_account_a_witch_once()
    {
        var accounts = new AccountService(new JsonAccountStore());

        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer" };
        RoundTrip(session, SteamLogin);
        RoundTrip(new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer" }, SteamLogin);

        var character = Assert.Single(accounts.Characters(session.Account!.Id));
        Assert.Equal("Wanderer", character.Name);
        Assert.Equal(CharacterClass.Witch, character.Class);
    }

    [Fact]
    public void A_default_character_name_taken_elsewhere_is_reported_and_the_login_goes_on()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var other = accounts.Register("alice", "pw1234").Value!;
        accounts.CreateCharacter(other.Id, "Wanderer", CharacterClass.Ranger);
        var notices = new List<string>();

        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer", Notice = notices.Add };
        var replies = RoundTrip(session, SteamLogin);

        Assert.Equal(LoginServerSession.ResultOk, Assert.IsType<LoginResultPoe2>(replies[0]).Result);
        Assert.Empty(accounts.Characters(session.Account!.Id));
        Assert.Contains("Wanderer", Assert.Single(notices));
    }

    [Fact]
    public void The_character_list_echoes_the_record_id_and_names_the_poe2_class_row()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer" };
        RoundTrip(session, SteamLogin);

        var list = Assert.IsType<CharacterListPoe2>(Assert.Single(RoundTrip(session, new U32Message(0x18, 7))));

        Assert.Equal(7u, list.U32_70);
        var entry = Assert.Single(list.Entries);
        Assert.Equal("Wanderer", entry.Name);
        Assert.Equal(1, entry.Level);
        Assert.Equal(1, entry.B4e);   // Characters.datc64 row 1: IntFour, the Witch
        Assert.Equal(0, list.B28);
    }

    [Theory]
    [InlineData(CharacterClass.Marauder, 0)]
    [InlineData(CharacterClass.Witch, 1)]
    [InlineData(CharacterClass.Ranger, 2)]
    [InlineData(CharacterClass.Duelist, 3)]
    [InlineData(CharacterClass.Shadow, 4)]
    [InlineData(CharacterClass.Templar, 5)]
    public void Poe1_classes_map_to_their_poe2_characters_rows(CharacterClass cls, byte row)
    {
        Assert.Equal(row, LoginServerSession.Poe2ClassRow(cls));
    }

    [Theory]
    [InlineData("Warrior")]
    [InlineData("Druid")]
    public void The_poe1_create_request_refuses_poe2_classes(string startState)
    {
        Assert.Null(LoginServerSession.ClassOf(new CreateCharacter("Brother", "Standard", 99, 99, startState, new byte[32])));
        Assert.Equal(CharacterClass.Shadow, LoginServerSession.ClassOf(new CreateCharacter("Brother", "Standard", 99, 99, "Shadow", new byte[32])));
    }

    [Fact]
    public void Poe2_has_no_scion_so_it_gets_the_witch_row()
    {
        Assert.Equal(1, LoginServerSession.Poe2ClassRow(CharacterClass.Scion));
    }

    [Fact]
    public void Steam_logins_at_once_create_one_account_and_one_character()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var notices = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 16, _ =>
            new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer", Notice = notices.Add }
                .Handle(SteamLogin));

        var account = accounts.LoginTrusted("steam").Value!;
        Assert.Single(accounts.Characters(account.AccountId));
        Assert.Empty(notices);
    }

    [Fact]
    public void An_invalid_steam_account_name_refuses_the_login()
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore())) { SteamAccount = "no" };

        var result = Assert.IsType<LoginResultPoe2>(session.Handle(SteamLogin).First());

        Assert.Equal(LoginServerSession.ResultAccountDoesNotExist, result.Result);
        Assert.True(session.Failed);
    }

    [Fact]
    public void A_list_sent_after_play_carries_the_record_id_of_the_last_request()
    {
        var store = new JsonAccountStore();
        var accounts = new AccountService(store);
        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer", Assign = _ => (0x3f70, 1) };
        RoundTrip(session, SteamLogin);
        RoundTrip(session, new U32Message(0x18, 9));
        // A permanently dead hardcore character cannot enter: Play answers with the list and a refusal.
        var character = accounts.Characters(session.Account!.Id)[0];
        store.UpdateCharacter(character with { HardcoreDeath = new("Hardcore", DateTimeOffset.UtcNow, true) });

        var replies = RoundTrip(session, new PlayPoe2(0, [new U32Pair(0, 0)]));

        Assert.Equal(9u, Assert.IsType<CharacterListPoe2>(replies[0]).U32_70);
        Assert.Equal(LoginServerSession.ResultLeagueDoesNotExist, Assert.IsType<U16Message>(replies[1]).Value);
    }

    [Fact]
    public void Play_sends_the_client_to_the_assigned_instance()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var session = new LoginServerSession(accounts)
        {
            SteamAccount = "steam", SteamCharacter = "Wanderer", Assign = _ => (0x3f70, 0x1234),
        };
        RoundTrip(session, SteamLogin);

        var info = Assert.IsType<InstanceInfo>(Assert.Single(RoundTrip(session, new PlayPoe2(0, [new U32Pair(0, 0)]))));

        Assert.Equal(0x3f70, info.AreaHash);
        Assert.Equal(0x1234u, info.U32_38);   // the ticket
        Assert.Equal("Wanderer", session.CharacterName);
    }

    [Fact]
    public void A_detail_request_for_a_listed_character_gets_its_empty_detail()
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore())) { SteamAccount = "steam" };
        RoundTrip(session, SteamLogin);
        RoundTrip(session, new U32Message(0x18, 7));

        var detail = Assert.IsType<CharacterDetailPoe2>(Assert.Single(RoundTrip(session, new CharacterDetailRequestPoe2(7, 0))));

        Assert.Equal(7u, detail.RecordId);
        Assert.Equal(0, detail.Index);
        Assert.Empty(detail.Data);
        Assert.Empty(detail.Pairs);
    }

    [Theory]
    [InlineData(8u, 0)]   // another login record
    [InlineData(7u, 1)]   // no second character
    public void A_detail_request_the_client_would_drop_gets_no_answer(uint record, byte index)
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore())) { SteamAccount = "steam" };
        RoundTrip(session, SteamLogin);
        RoundTrip(session, new U32Message(0x18, 7));

        Assert.Empty(session.Handle(new CharacterDetailRequestPoe2(record, index)));
    }

    [Fact]
    public void Play_enters_the_character_the_first_pair_indexes()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer", Assign = _ => (0x3f70, 1) };
        RoundTrip(session, SteamLogin);
        accounts.CreateCharacter(session.Account!.Id, "Second", CharacterClass.Monk);
        var list = Assert.IsType<CharacterListPoe2>(Assert.Single(RoundTrip(session, new U32Message(0x18, 0))));
        int second = list.Entries.ToList().FindIndex(e => e.Name == "Second");

        RoundTrip(session, new PlayPoe2(0, [new U32Pair((uint)second, 0)]));

        Assert.Equal(10, list.Entries[second].B4e);   // the Monk's row
        Assert.Equal("Second", session.CharacterName);
        Assert.Equal(CharacterClass.Monk, session.CharacterClass);
    }

    static CreateCharacterPoe2 Create(string name, string startState = "DexIntFourBDefault", string league = "Standard") =>
        new(name, league, 7, 0, 0, startState, new byte[32]);

    [Fact]
    public void Creating_a_character_sends_the_list_with_it_then_the_result()
    {
        var accounts = new AccountService(new JsonAccountStore());
        var session = new LoginServerSession(accounts) { SteamAccount = "steam", SteamCharacter = "Wanderer" };
        RoundTrip(session, SteamLogin);
        RoundTrip(session, new U32Message(0x18, 7));

        var replies = RoundTrip(session, Create("Brother"));

        var list = Assert.IsType<CharacterListPoe2>(replies[0]);
        Assert.Equal(7u, list.U32_70);
        var entry = Assert.Single(list.Entries, e => e.Name == "Brother");
        Assert.Equal(10, entry.B4e);   // the Monk's row
        Assert.Equal(0, entry.B51 & 0x08);   // bit 3 hides an entry from the create screen's lookup
        var result = Assert.IsType<CreateCharacterResultPoe2>(replies[1]);
        Assert.Equal(LoginServerSession.ResultOk, result.Result);
        Assert.Equal(7u, result.RecordId);
        Assert.False(result.Tracking);
        var saved = Assert.Single(accounts.Characters(session.Account!.Id), c => c.Name == "Brother");
        Assert.Equal(CharacterClass.Monk, saved.Class);
        Assert.Equal("Standard", saved.League);
    }

    [Theory]
    [InlineData("Brother", "WitchActTwo", "Standard", LoginServerSession.ResultInvalidClass)]
    [InlineData("Brother", "IntFourDefault", "Allflame", LoginServerSession.ResultLeagueDoesNotExist)]
    [InlineData("Brother", "IntFourDefault", "Ruthless", LoginServerSession.ResultLeagueDoesNotExist)]
    [InlineData("Wanderer", "IntFourDefault", "Standard", LoginServerSession.ResultCharacterNameTaken)]
    [InlineData("Bo", "IntFourDefault", "Standard", LoginServerSession.ResultCharacterNameTooShort)]
    public void A_refused_creation_sends_only_the_result(string name, string startState, string league, ushort expected)
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore())) { SteamAccount = "steam", SteamCharacter = "Wanderer" };
        RoundTrip(session, SteamLogin);

        var result = Assert.IsType<CreateCharacterResultPoe2>(Assert.Single(RoundTrip(session, Create(name, startState, league))));

        Assert.Equal(expected, result.Result);
    }

    [Fact]
    public void A_creation_before_a_login_gets_no_answer()
    {
        Assert.Empty(new LoginServerSession(new AccountService(new JsonAccountStore())).Handle(Create("Brother")));
    }

    [Fact]
    public void Poe2_requests_before_a_login_get_no_answer()
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore())) { Assign = _ => (0x3f70, 1) };

        Assert.Empty(session.Handle(new U32Message(0x18, 0)));
        Assert.Empty(session.Handle(new PlayPoe2(0, [])));
        Assert.Empty(session.Handle(new CharacterDetailRequestPoe2(0, 0)));
    }

    [Fact]
    public void Without_a_steam_account_a_token_login_fails()
    {
        var session = new LoginServerSession(new AccountService(new JsonAccountStore()));

        var result = Assert.IsType<LoginResultPoe2>(session.Handle(SteamLogin).First());

        Assert.Equal(LoginServerSession.ResultAccountDoesNotExist, result.Result);
        Assert.True(session.Failed);
    }
}
