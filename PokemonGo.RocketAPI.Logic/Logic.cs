using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AllEnum;
using PokemonGo.RocketAPI.Enums;
using PokemonGo.RocketAPI.Extensions;
using PokemonGo.RocketAPI.GeneratedCode;
using PokemonGo.RocketAPI.Logic.Utils;

namespace PokemonGo.RocketAPI.Logic
{
    using System.Collections;

    public class Logic
    {
        private readonly Client _client;
        private readonly ISettings _clientSettings;
        private readonly Inventory _inventory;

        public Logic(ISettings clientSettings)
        {
            this._clientSettings = clientSettings;
            this._client = new Client(this._clientSettings);
            this._inventory = new Inventory(this._client);
        }

        public async void Execute()
        {
            Console.WriteLine($"Starting Execute on login server: {this._clientSettings.AuthType}");
            
            if (this._clientSettings.AuthType == AuthType.Ptc)
                await this._client.DoPtcLogin(this._clientSettings.PtcUsername, this._clientSettings.PtcPassword);
            else if (this._clientSettings.AuthType == AuthType.Google)
                await this._client.DoGoogleLogin();

            while (true)
            {
                try
                {
                    await this._client.SetServer();
                    await this.ExecuteFarmingPokestopsAndPokemons(this._client);

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Exception: {ex}");
                }

                await Task.Delay(10000);
            }
        }

        public async Task RepeatAction(int repeat, Func<Task> action)
        {
            for (int i = 0; i < repeat; i++)
                await action();
        }

        private async Task ExecuteFarmingPokestopsAndPokemons(Client client)
        {
            var mapObjects = await client.GetMapObjects();

            var pokeStops = mapObjects.MapCells.SelectMany(i => i.Forts).Where(i => i.Type == FortType.Checkpoint && i.CooldownCompleteTimestampMs < DateTime.UtcNow.ToUnixTime());

            foreach (var pokeStop in pokeStops)
            {
                var update = await client.UpdatePlayerLocation(pokeStop.Latitude, pokeStop.Longitude);
                var fortInfo = await client.GetFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);
                var fortSearch = await client.SearchFort(pokeStop.Id, pokeStop.Latitude, pokeStop.Longitude);

                Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] Farmed XP: {fortSearch.ExperienceAwarded}, Gems: { fortSearch.GemsAwarded}, Eggs: {fortSearch.PokemonDataEgg} Items: {StringUtils.GetSummedFriendlyNameOfItemAwardList(fortSearch.ItemsAwarded)}");

                await Task.Delay(15000);
                await this.ExecuteCatchAllNearbyPokemons(client);
            }
        }

        private async Task ExecuteCatchAllNearbyPokemons(Client client)
        {
            var mapObjects = await client.GetMapObjects();

            var pokemons = mapObjects.MapCells.SelectMany(i => i.CatchablePokemons);

            foreach (var pokemon in pokemons)
            {
                var update = await client.UpdatePlayerLocation(pokemon.Latitude, pokemon.Longitude);
                var encounterPokemonResponse = await client.EncounterPokemon(pokemon.EncounterId, pokemon.SpawnpointId);
                var pokemonCP = encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp;
                var pokeball = await this.GetBestBall(pokemonCP);

                CatchPokemonResponse caughtPokemonResponse;
                do
                {
                    var legandaryPokemonTypes = new[]
                    {
                        // Epics
                        PokemonId.Dragonite,
                        PokemonId.Snorlax,
                        PokemonId.Blastoise,
                        PokemonId.Charizard,
                        PokemonId.Venusaur,
                        PokemonId.Victreebell,
                        PokemonId.Rapidash,
                        PokemonId.Porygon,
                        PokemonId.Nidoqueen,
                        PokemonId.Nidoking,
                        PokemonId.Lapras,
                        PokemonId.Dewgong,
                        PokemonId.Cloyster,
                        PokemonId.Alakhazam,
                        PokemonId.Clefable,
                        PokemonId.Tauros,
                        // Legends
                        PokemonId.Mew,
                        PokemonId.Mewtwo,
                        PokemonId.Zapdos,
                        PokemonId.Ditto,
                        PokemonId.Moltres,
                    };

                    foreach (var wantedPokemonType in legandaryPokemonTypes)
                    {
                        if (wantedPokemonType == pokemon.PokemonId)
                        {
                            Console.WriteLine($"Found: {wantedPokemonType}");
                            if (MiscEnums.Item.ITEM_MASTER_BALL > 0)
                            {
                                pokeball = MiscEnums.Item.ITEM_MASTER_BALL;
                                Console.WriteLine("Threw master ball");
                            }

                            if (MiscEnums.Item.ITEM_GREAT_BALL > 0)
                            {
                                pokeball = MiscEnums.Item.ITEM_GREAT_BALL;
                            }

                        }
                    }

                    caughtPokemonResponse = await client.CatchPokemon(pokemon.EncounterId, pokemon.SpawnpointId, pokemon.Latitude, pokemon.Longitude, pokeball);

                    Console.WriteLine($"Log CP: {encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp} - {pokeball}");
                }
                while (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchMissed);

                Console.WriteLine(caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess ? $"[{DateTime.Now.ToString("HH:mm:ss")}] We caught a {pokemon.PokemonId} with CP {encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp}" : $"[{DateTime.Now.ToString("HH:mm:ss")}] {pokemon.PokemonId} with CP {encounterPokemonResponse?.WildPokemon?.PokemonData?.Cp} got away..");

                if (caughtPokemonResponse.Status == CatchPokemonResponse.Types.CatchStatus.CatchSuccess)
                {
                    var inventory = await _client.GetInventory();
                    var pokemons1 =
                        inventory.InventoryDelta.InventoryItems
                            .Select(i => i.InventoryItemData?.Pokemon)
                            .First(p => p != null && p?.PokemonId == pokemon.PokemonId);

                    await EvolveAllGivenPokemons(_client, new[] { pokemons1 });
                    await TransferAllButStrongestUnwantedPokemon(this._client);
                }

                await Task.Delay(5000);
            }
        }

        private async Task<MiscEnums.Item> GetBestBall(int? pokemonCP)
        {
            var inventory = await this._client.GetInventory();

            var ballCollection = inventory.InventoryDelta.InventoryItems
                   .Select(i => i.InventoryItemData?.Item)
                   .Where(p => p != null)
                   .GroupBy(i => (MiscEnums.Item)i.Item_)
                   .Select(kvp => new { ItemId = kvp.Key, Amount = kvp.Sum(x => x.Count) })
                   .Where(y => y.ItemId == MiscEnums.Item.ITEM_POKE_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_GREAT_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_ULTRA_BALL
                            || y.ItemId == MiscEnums.Item.ITEM_MASTER_BALL);

            var pokeBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_POKE_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_POKE_BALL, Amount = 0 }).FirstOrDefault().Amount;

            var greatBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_GREAT_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_GREAT_BALL, Amount = 0 }).FirstOrDefault().Amount;

            var ultraBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_ULTRA_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_ULTRA_BALL, Amount = 0 }).FirstOrDefault().Amount;

            var masterBallsCount = ballCollection.Where(p => p.ItemId == MiscEnums.Item.ITEM_MASTER_BALL).
                DefaultIfEmpty(new { ItemId = MiscEnums.Item.ITEM_MASTER_BALL, Amount = 0 }).FirstOrDefault().Amount;

            if (masterBallsCount > 0 && pokemonCP >= 1000)
            {
                return MiscEnums.Item.ITEM_MASTER_BALL;
            }

            if (ultraBallsCount > 0 && pokemonCP >= 1000)
            {
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            }
            if (greatBallsCount > 0 && pokemonCP >= 1000)
            {
                return MiscEnums.Item.ITEM_GREAT_BALL;
            }

            if (ultraBallsCount > 0 && pokemonCP >= 600)
            {
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            }
            else if (greatBallsCount > 0 && pokemonCP >= 600)
            {
                return MiscEnums.Item.ITEM_GREAT_BALL;
            }

            if (greatBallsCount > 0 && pokemonCP >= 350)
            {
                return MiscEnums.Item.ITEM_GREAT_BALL;
            }

            if (pokeBallsCount > 0)
            {
                return MiscEnums.Item.ITEM_POKE_BALL;
            }
            if (greatBallsCount > 0)
            {
                return MiscEnums.Item.ITEM_GREAT_BALL;
            }
            if (ultraBallsCount > 0)
            {
                return MiscEnums.Item.ITEM_ULTRA_BALL;
            }
            if (masterBallsCount > 0)
            {
                return MiscEnums.Item.ITEM_MASTER_BALL;
            }

            return MiscEnums.Item.ITEM_POKE_BALL;
        }

        private static async Task EvolveAllGivenPokemons(Client client, IEnumerable<PokemonData> pokemonToEvolve)
        {
            foreach (var pokemon in pokemonToEvolve)
            {
                var countOfEvolvedUnits = 0;
                var xpCount = 0;

                EvolvePokemonOut evolvePokemonOutProto;
                var blacklistEvolvePokemonTypes = new[]
                {
                    PokemonId.Goldeen
                };

                foreach (var unwantedPokemonType in blacklistEvolvePokemonTypes)
                {
                    var pokemonOfDesiredType = pokemonToEvolve.Where(p => p.PokemonId == unwantedPokemonType)
                                                .ToList();

                    if (pokemonOfDesiredType.Any())
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"Blacklisted: {pokemon.PokemonId}");
                        Console.ForegroundColor = ConsoleColor.White;
                        return;
                    }
                }

                do
                {
                    evolvePokemonOutProto = await client.EvolvePokemon(pokemon.Id);
                    //todo: someone check whether this still works

                    if (evolvePokemonOutProto.Result == (EvolvePokemonOut.Types.EvolvePokemonStatus)1)
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] Evolved {pokemon.PokemonId} successfully for {evolvePokemonOutProto.ExpAwarded}xp");
                        Console.ForegroundColor = ConsoleColor.White;
                        countOfEvolvedUnits++;
                        xpCount += evolvePokemonOutProto.ExpAwarded;
                    }
                    else
                    {
                        var result = evolvePokemonOutProto.Result;

                        Console.WriteLine($"Failed to evolve {pokemon.PokemonId}. " +
                                                 $"EvolvePokemonOutProto.Result was {result}");
                        Console.WriteLine($"Due to above error, stopping evolving {pokemon.PokemonId}");
                       
                    }
                }
                while (evolvePokemonOutProto.Result == (EvolvePokemonOut.Types.EvolvePokemonStatus)1 && (evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.FailedInsufficientResources || evolvePokemonOutProto.Result == EvolvePokemonOut.Types.EvolvePokemonStatus.FailedPokemonCannotEvolve));
                if (countOfEvolvedUnits > 0)
                    Console.WriteLine($"[{DateTime.Now.ToString("HH:mm:ss")}] Evolved {countOfEvolvedUnits} piece of {pokemon.PokemonId} for {xpCount}xp");

                await Task.Delay(3000);
            }
        }

        private static async Task TransferAllButStrongestUnwantedPokemon(Client client)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] firing up the meat grinder");
            Console.ForegroundColor = ConsoleColor.White;
            var unwantedPokemonTypes = new[]
            {
                PokemonId.Pidgey,
                PokemonId.Rattata,
                PokemonId.Weedle,
                PokemonId.Zubat,
                PokemonId.Caterpie,
                PokemonId.Pidgeotto,
                PokemonId.NidoranFemale,
                PokemonId.Paras,
                PokemonId.Venonat,
                PokemonId.Psyduck,
                PokemonId.Poliwag,
                PokemonId.Slowpoke,
                PokemonId.Drowzee,
                PokemonId.Gastly,
                PokemonId.Goldeen,
                PokemonId.Staryu,
                PokemonId.Magikarp,
                PokemonId.Eevee,
                PokemonId.Kakuna,
                PokemonId.Krabby,
                PokemonId.Spearow,
                PokemonId.Raticate,
                PokemonId.Zubat,
                PokemonId.Metapod,
                PokemonId.Dratini,
                PokemonId.Pidgeot,
                PokemonId.Poliwhirl,
                PokemonId.Voltorb,
                PokemonId.Horsea,
                PokemonId.Seaking
            };

            var inventory = await client.GetInventory();
            var pokemons = inventory.InventoryDelta.InventoryItems
                                .Select(i => i.InventoryItemData?.Pokemon)
                                .Where(p => p != null && p?.PokemonId > 0)
                                .ToArray();

            foreach (var unwantedPokemonType in unwantedPokemonTypes)
            {
                var pokemonOfDesiredType = pokemons.Where(p => p.PokemonId == unwantedPokemonType)
                                                   .OrderByDescending(p => p.Cp)
                                                   .ToList();

                var unwantedPokemon = pokemonOfDesiredType.Skip(1).ToList();

                await TransferAllGivenPokemons(client, unwantedPokemon);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[!] finished grinding all the meat");
            Console.ForegroundColor = ConsoleColor.White;
        }

        private static async Task TransferAllGivenPokemons(Client client, IEnumerable<PokemonData> unwantedPokemons)
        {
            foreach (var pokemon in unwantedPokemons)
            {
                var transferPokemonResponse = await client.TransferPokemon(pokemon.Id);
                if (transferPokemonResponse.Status == 1)
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.WriteLine($"Shoved another {pokemon.PokemonId} ({pokemon.Cp}CP) down the meat grinder");
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    var status = transferPokemonResponse.Status;

                    Console.WriteLine($"Somehow failed to grind {pokemon.PokemonId}. " +
                                             $"ReleasePokemonOutProto.Status was {status}");
                }

                await Task.Delay(3000);
            }
        }

        private async Task TransferDuplicatePokemon()
        {
            Console.WriteLine($"Transfering duplicate Pokemon");

            var duplicatePokemons = await this._inventory.GetDuplicatePokemonToTransfer();
            foreach (var duplicatePokemon in duplicatePokemons)
            {
                var transfer = await this._client.TransferPokemon(duplicatePokemon.Id);
                Console.WriteLine($"Transfer {duplicatePokemon.PokemonId} with {duplicatePokemon.Cp})");
                await Task.Delay(500);
            }
        }
    }
}
