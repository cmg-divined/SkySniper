using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Sniper.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Minio.DataModel;
using Newtonsoft.Json;

namespace Coflnet.Sky.Sniper.Services
{
    public interface IPersitanceManager
    {
        Task LoadLookups();
        Task SaveLookups();
    }
    public class SniperService : BackgroundService
    {
        public ConcurrentDictionary<string, PriceLookup> Lookups = new ConcurrentDictionary<string, PriceLookup>();
        private IConfiguration config;
        private IPersitanceManager persitance;

        public event Action<LowPricedAuction> FoundSnipe;
        private HashSet<string> IncludeKeys = new HashSet<string>()
        {
            "backpack_color",
            // potion "level", // not engough impact
            // "item_tier", // mostly found on armor, unsure what it does
            "talisman_enrichment", // talismans can be enriched with additional stats
            "drill_part_engine",
            "drill_part_fuel_tank",
            // deemend to low difference "drill_part_upgrade_module",
            // could not observe price change "ability_scroll", // applied to hyperions
            // magma armor is to cheap "magmaCubesKilled"
            "captured_player", // cake soul 
            "event", // year+eventtype
            "wood_singularity_count", // self explanatory
            "art_of_war_count", //       ^^
            "new_years_cake", // year of the cake
            "heldItem", // pet held item
            "skin", // cosmetic skins
            "candyUsed", // candy count
            "exp", // collected experience of pets
            "rarity_upgrades", // recomb
            "winning_bid", // price paid for midas
            "dungeon_item_level", // "stars"
            "farming_for_dummies_count",
            "unlocked_slots", // available gemstone slots
            "gemstone_slots", // old unlocked slots
            "zombie_kills", // slayer kills
            "spider_kills", // slayer kills
            "eman_kills", // slayer kills
            "expertise_kills", // unkown kind of kills
            "bow_kills", // huricane bow
            "raider_kills", // raiders axe
            "sword_kills", 
            "ethermerge", 
            "edition", // great spook stuff
            "hpc", // hot potato books
            "tuned_transmission", // aotv upgrade
            "power_ability_scroll", // i have no clue
        };
        /** NOTES
        yogsKilled - needs further be looked into
        skeletorKills - to low volume to include 
        farmed_cultivating - tells the state of the cultivating enchant (already taken care of)

        */
        /* select helper
SELECT l.AuctionId,l.KeyId,l.Value,a.StartingBid, a.HighestBidAmount,a.Uuid,a.Tag,a.End FROM `NBTLookups` l, Auctions a
where KeyId = 128
and auctionid > 305000000  
and AuctionId = a.Id  
ORDER BY l.`AuctionId`  DESC;

        */

        // stuff changing value by 5+M
        private HashSet<string> VeryValuable = new HashSet<string>()
        {
            "rarity_upgrades",
            "winning_bid",
            "skin"

        };


        public SniperService(IConfiguration config, IPersitanceManager persitance)
        {
            this.config = config;
            this.persitance = persitance;
        }

        public int GetPrice(hypixel.SaveAuction auction)
        {
            if (TryGetReferenceAuctions(auction, out ReferenceAuctions bucket))
            {
                if (bucket.LastLbin.Price != 0)
                    return bucket.LastLbin.Price;
                return bucket.Price;
            }
            return 0;
        }

        public IEnumerable<long> GetReferenceUids(hypixel.SaveAuction auction)
        {
            if (TryGetReferenceAuctions(auction, out ReferenceAuctions bucket))
                return bucket.References.Select(r => r.AuctionId);
            return new long[0];
        }

        /// <summary>
        /// Adds persisted loockup data
        /// </summary>
        /// <param name="itemTag"></param>
        /// <param name="loadedVal"></param>
        public void AddLookupData(string itemTag, PriceLookup loadedVal)
        {
            Lookups.AddOrUpdate(itemTag, loadedVal,(key,value)=>{
                foreach (var item in loadedVal.Lookup)
                {
                    if(!value.Lookup.TryGetValue(item.Key, out ReferenceAuctions existingBucket))
                    {
                        value.Lookup[item.Key] = item.Value;
                        continue;
                    }
                    existingBucket.References = item.Value.References;
                    existingBucket.Price = item.Value.Price;
                    if(existingBucket.LastLbin.Price == default)
                        existingBucket.LastLbin = item.Value.LastLbin;
                    if(existingBucket.SecondLbin.Price == default)
                        existingBucket.SecondLbin = item.Value.SecondLbin;
                }
                return value;
            });
        }

        public void AddSoldItem(hypixel.SaveAuction auction)
        {
            if (!Lookups.ContainsKey(auction.Tag))
            {
                Lookups[auction.Tag] = new PriceLookup();
            }
            if (!TryGetReferenceAuctions(auction, out ReferenceAuctions bucket))
            {
                bucket = CreateAndAddBucket(auction);
            }
            if (bucket.References.Where(r => r.AuctionId == auction.UId).Any())
                return;
            bucket.References.Enqueue(CreateReferenceFromAuction(auction));
            if (bucket.LastLbin.AuctionId == auction.UId)
            {
                bucket.LastLbin.Price = 0; // the lowest bin was sold
            }
            var size = bucket.References.Count;
            if (size > 120)
                bucket.References.TryDequeue(out ReferencePrice ra);
            var deduplicated = bucket.References
                .OrderByDescending(b => b.Day)
                .Take(30)
                .GroupBy(a => a.Seller)
                .Select(a => a.Last())  // only use one (the last) price from each seller
                .ToList();
            size = deduplicated.Count();
            bucket.Price = deduplicated
                .Take(9)
                .OrderByDescending(b => b.Price)
                .Skip(size < 5 ? size / 2 : 4)
                .Select(b => b.Price)
                .First();
        }

        private ReferenceAuctions CreateAndAddBucket(hypixel.SaveAuction auction, int dropLevel = 0)
        {
            ReferenceAuctions bucket = new ReferenceAuctions();
            Lookups[auction.Tag].Lookup.TryAdd(KeyFromSaveAuction(auction, dropLevel), bucket);
            return bucket;
        }

        private static ReferencePrice CreateReferenceFromAuction(hypixel.SaveAuction auction)
        {
            return new ReferencePrice()
            {
                AuctionId = auction.UId,
                Day = (short)(DateTime.Now - new DateTime(2021, 9, 25)).TotalDays,
                Price = (int)(auction.HighestBidAmount == 0 ? auction.StartingBid : auction.HighestBidAmount),
                Seller = auction.AuctioneerId == null ? (short)(auction.SellerId % (2 << 14)) : Convert.ToInt16(auction.AuctioneerId.Substring(0, 4), 16)
            };
        }

        private ReferenceAuctions GetReferenceAuctions(hypixel.SaveAuction auction)
        {
            return Lookups[auction.Tag].Lookup[KeyFromSaveAuction(auction)];
        }

        private bool TryGetReferenceAuctions(hypixel.SaveAuction auction, out ReferenceAuctions bucket)
        {
            bucket = null;
            if (!Lookups.TryGetValue(auction.Tag, out PriceLookup lookup))
                return false;
            var l = lookup.Lookup;
            if (l.TryGetValue(KeyFromSaveAuction(auction), out bucket))
                return true;
            if (l.TryGetValue(KeyFromSaveAuction(auction, 1), out bucket))
                return true;
            return l.TryGetValue(KeyFromSaveAuction(auction, 2), out bucket);
        }


        private AuctionKey KeyFromSaveAuction(hypixel.SaveAuction auction, int dropLevel = 0)
        {
            var key = new AuctionKey();


            key.Reforge = Coflnet.Sky.Constants.RelevantReforges.Contains(auction.Reforge) ? auction.Reforge : hypixel.ItemReferences.Reforge.Any;
            if (dropLevel == 0)
            {
                key.Enchants = auction.Enchantments
                    ?.Where(e => e.Level >= 6 || Coflnet.Sky.Constants.RelevantEnchants.Where(el => el.Type == e.Type && el.Level >= e.Level).Any())
                    .Select(e => new Enchantment() { Lvl = e.Level, Type = e.Type }).ToList();

                key.Modifiers = auction.FlatenedNBT?.Where(n => IncludeKeys.Contains(n.Key) || n.Value == "PERFECT")
                                .OrderByDescending(n => n.Key)
                                .Select(NormalizeData).ToList();
            }
            else if (dropLevel == 1)
            {
                key.Modifiers = auction.FlatenedNBT?.Where(n => VeryValuable.Contains(n.Key) || n.Value == "PERFECT")
                            .OrderByDescending(n => n.Key)
                            .ToList();
                key.Enchants = auction.Enchantments
                    ?.Where(e => e.Level > 6 || Coflnet.Sky.Constants.RelevantEnchants.Where(el => el.Type == e.Type && el.Level >= e.Level).Any())
                    .Select(e => new Enchantment() { Lvl = e.Level, Type = e.Type }).ToList();
            }
            else
            {
                //key.Modifiers = new List<KeyValuePair<string, string>>();
                key.Enchants = new List<Enchantment>();
            }

            key.Tier = auction.Tier;
            key.Count = (byte)auction.Count;

            return key;
        }

        private static KeyValuePair<string, string> NormalizeData(KeyValuePair<string, string> s)
        {
            if (s.Key == "exp")
                return NormalizeNumberTo(s, 1_000_000);
            if (s.Key == "winning_bid")
                return NormalizeNumberTo(s, 10_000_000);
            if (s.Key.EndsWith("_kills"))
                return NormalizeNumberTo(s, 10_000);
            if (s.Key == "candyUsed") // all candied are the same
                return new KeyValuePair<string, string>(s.Key, (double.Parse(s.Value) > 0 ? 1 : 0).ToString());
            if (s.Key == "edition")
            {
                var val = int.Parse(s.Value);
                if (val < 100)
                    return new KeyValuePair<string, string>(s.Key, "0");
                if (val < 1000)
                    return new KeyValuePair<string, string>(s.Key, "1000");
                if (val < 10000)
                    return new KeyValuePair<string, string>(s.Key, "10k");
                return new KeyValuePair<string, string>(s.Key, "100k");
            }
            if (s.Key == "hpc")
                return NormalizeNumberTo(s, 15);

            return s;
        }

        private static KeyValuePair<string, string> NormalizeNumberTo(KeyValuePair<string, string> s, int groupingSize)
        {
            return new KeyValuePair<string, string>(s.Key, (((int)double.Parse(s.Value)) / groupingSize).ToString());
        }

        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Task newAuctions = ConsumeNewAuctions(stoppingToken);
            Task soldAuctions = LoadLookupsAndProcessSells(stoppingToken);

            return Task.WhenAll(newAuctions, soldAuctions);
        }

        private Task ConsumeNewAuctions(CancellationToken stoppingToken)
        {
            return Kafka.KafkaConsumer.Consume<hypixel.SaveAuction>(config["KAFKA_HOST"], config["TOPICS:NEW_AUCTION"], a =>
            {
                TestNewAuction(a);
                return Task.CompletedTask;
            }, stoppingToken, "sky-sniper");
        }

        public void TestNewAuction(hypixel.SaveAuction auction)
        {
            var lookup = Lookups.GetOrAdd(auction.Tag, key => new PriceLookup());
            var l = lookup.Lookup;
            var cost = auction.StartingBid;
            var lbinPrice = auction.StartingBid * 1.05;
            var medPrice = auction.StartingBid * 1.1;
            for (int i = 0; i < 3; i++)
            {
                if (!l.TryGetValue(KeyFromSaveAuction(auction, i), out ReferenceAuctions bucket))
                {
                    if (i != 0)
                        continue;
                    bucket = CreateAndAddBucket(auction);
                }
                // only trigger lbin if also below median or median is not set
                if (bucket.LastLbin.Price > lbinPrice && (bucket.Price > lbinPrice || bucket.Price == 0))
                {
                    FoundAFlip(auction, bucket, LowPricedAuction.FinderType.SNIPER, bucket.LastLbin.Price);
                    i += 10;
                }
                else if (bucket.Price > medPrice)
                {
                    FoundAFlip(auction, bucket, LowPricedAuction.FinderType.MEDIAN_SNIPER, bucket.Price);
                    i += 10;
                }

                // update lbin
                if (bucket.LastLbin.Price > cost || bucket.LastLbin.Price == 0)
                {
                    bucket.SecondLbin = bucket.LastLbin;
                    bucket.LastLbin = CreateReferenceFromAuction(auction);
                }
                else if (bucket.SecondLbin.Price == 0 || bucket.SecondLbin.Price > cost)
                {
                    // set second lbin to fallback when lbin is sold
                    bucket.SecondLbin = CreateReferenceFromAuction(auction);
                }
            }
        }

        private void FoundAFlip(hypixel.SaveAuction auction, ReferenceAuctions bucket, LowPricedAuction.FinderType type, int targetPrice)
        {
            FoundSnipe.Invoke(new LowPricedAuction()
            {
                Auction = auction,
                Finder = type,
                TargetPrice = targetPrice,
                DailyVolume = bucket.References.LastOrDefault().Day
            });
        }

        private async Task LoadLookupsAndProcessSells(CancellationToken stoppingToken)
        {
            try
            {
                await persitance.LoadLookups();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Console.WriteLine(e.StackTrace);
            }
            Console.WriteLine("loaded lookup");
            await Kafka.KafkaConsumer.Consume<hypixel.SaveAuction>(config["KAFKA_HOST"], config["TOPICS:SOLD_AUCTION"], async a =>
            {
                AddSoldItem(a);
                if (a.UId % 10000 == 0)
                {
                    Console.WriteLine("consumed sold");
                    try
                    {
                        await persitance.SaveLookups();
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("could not save " + e.Message);
                        Console.WriteLine(e.StackTrace);
                    }
                }
            }, stoppingToken, "sky-sniper");
            // persist before exiting
            await persitance.SaveLookups();
        }
    }
}