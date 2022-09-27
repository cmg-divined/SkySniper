using System.Collections.Generic;
using Coflnet.Sky.Core;
using Coflnet.Sky.Sniper.Services;
using NUnit.Framework;

namespace Coflnet.Sky.Sniper.Models
{
    public class AuctionkeyTests
    {
        [Test]
        public void DifferentModifiersDecrease()
        {
            var key = new AuctionKey();
            var keyB = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>("test", "test") } };
            // by default reforge and tier match
            Assert.Greater(key.Similarity(key), keyB.Similarity(key));
        }
        [Test]
        public void SameModsMatch()
        {
            var key = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>("test", "test") } };
            var keyB = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>("test", "test") } };
            // by default reforge and tier match
            Assert.AreEqual(key.Similarity(key), keyB.Similarity(key));
        }
        [Test]
        public void SameModsDecreaseFurther()
        {
            var key = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>("test", "testxy") } };
            var keyB = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() { new KeyValuePair<string, string>("test", "test") } };
            // by default reforge and tier match
            Assert.Greater(key.Similarity(key), keyB.Similarity(key));
        }
        [Test]
        public void NoModsNoError()
        {
            var key = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() };
            var keyB = new AuctionKey() { Modifiers = new List<KeyValuePair<string, string>>() };
            // by default reforge and tier match
            Assert.AreEqual(key.Similarity(key), keyB.Similarity(key));
        }
        [Test]
        public void DifferentEnchantsDecrease()
        {
            var key = new AuctionKey();
            var keyB = new AuctionKey() { Enchants = new List<Enchantment>() { new Enchantment() { Lvl = 1, Type = Core.Enchantment.EnchantmentType.angler } } };
            // by default reforge and tier match
            Assert.Greater(key.Similarity(key), keyB.Similarity(key), "extra enchants should decrease");
        }
        [Test]
        public void RecombCadyRelicLbinSimilarity()
        {
            // the issue likely has something to do with enrichments, TODO: add enrichments
            var auctionA = new SaveAuction() { FlatenedNBT = new(), Tag = "CANDY_RELIC", Tier = Tier.LEGENDARY };
            var b = Services.SniperServiceTests.Dupplicate(auctionA);
            b.FlatenedNBT.Add("rarity_upgrades", "1");
            b.Tier = Tier.MYTHIC;
            var sniperService = new SniperService();
            var keyA = sniperService.KeyFromSaveAuction(auctionA);
            var keyB = sniperService.KeyFromSaveAuction(b);
            Assert.Less(keyA.Similarity(keyB), keyA.Similarity(keyA));
        }
        [Test]
        public void IgnoresBadEnchants()
        {
            var key = new AuctionKey() { Reforge = ItemReferences.Reforge.Any, Enchants = new List<Enchantment>(), Modifiers = new() };
            key.Enchants.Add(new() { Type = Core.Enchantment.EnchantmentType.execute, Lvl = 8 });
            System.Console.WriteLine(key);
            var auction = new SaveAuction()
            {
                Enchantments = new() {
                new() { Level = 6, Type = Core.Enchantment.EnchantmentType.luck },
                 new() { Level = 8, Type = Core.Enchantment.EnchantmentType.execute }
             }
            };
            var service = new SniperService();
            // by default reforge and tier match
            Assert.AreEqual(key, service.KeyFromSaveAuction(auction));
        }
    }

}