﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Sniper.Models;
using Coflnet.Sky.Sniper.Services;
using Coflnet.Sky.Core.Prediction;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Sniper.Controllers
{
    [ApiController]
    [Route("/api/[controller]")]
    public class SniperController : ControllerBase
    {
        private readonly ILogger<SniperController> _logger;
        private SniperService service;
        private ITokenService tokenService;

        public SniperController(ILogger<SniperController> logger, SniperService service, ITokenService tokenService)
        {
            _logger = logger;
            this.service = service;
            this.tokenService = tokenService;
        }

        [HttpGet]
        [Route("lookup")]
        [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any, NoStore = false)]
        public IEnumerable<string> GetIds()
        {
            return service.Lookups.Keys;
        }

        /// <summary>
        /// Retrieve item lookup state transfer
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="Authorization"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("lookup/{itemId}")]
        public string GetLookup(string itemId, [FromHeader] string Authorization)
        {
            CountUsageAndValidate(Authorization);
            return Convert.ToBase64String(MessagePack.MessagePackSerializer.Serialize(service.Lookups[itemId]));
        }

        /// <summary>
        /// Retrieve item lookup state transfer
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="Authorization"></param>
        /// <returns></returns>
        [HttpGet]
        [Route("dump/{itemId}")]
        public PriceLookup GetLookupJson(string itemId, [FromHeader] string Authorization)
        {
            CountUsageAndValidate(Authorization);
            return service.Lookups[itemId];
        }

        private void CountUsageAndValidate(string Authorization)
        {
            try
            {
                if (!tokenService.HasTokenAccess(Authorization))
                    throw new CoflnetException("invalid_token", "The passed access token is invalid (limit probably used up)");
            }
            catch (JWT.Exceptions.TokenExpiredException)
            {
                throw new CoflnetException("token_expired", "The passed access token is no longer valid");
            }
            catch (System.FormatException)
            {
                throw new CoflnetException("invalid_token", "The passed access token is invalid");
            }
        }

        [HttpGet]
        [Route("token")]
        public string Token()
        {
            return tokenService.CreateToken();
        }


        /// <summary>
        /// Retrieve lookup references
        /// </summary>
        /// <param name="itemId"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        [HttpPost]
        [Route("references")]
        public IEnumerable<long> GetLookups(SaveAuction auction)
        {
            return service.GetReferenceUids(auction);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="auctions"></param>
        /// <returns></returns>
        [Route("price")]
        [HttpPost]
        public IEnumerable<PriceEstimate> GetPrices(IEnumerable<SaveAuction> auctions)
        {
            if (auctions == null)
                return new List<PriceEstimate>();
            return auctions.Select(a =>
            {
                try
                {
                    Console.WriteLine("a: " + Newtonsoft.Json.JsonConvert.SerializeObject(a));
                    return service.GetPrice(a);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "getting price for auction");
                }
                return new PriceEstimate();
            });
        }

        /// <summary>
        /// Auction array as base64 encoded LZ4MessagePack
        /// </summary>
        /// <param name="auctions"></param>
        /// <returns></returns>
        [Route("prices")]
        [HttpPost]
        public IEnumerable<PriceEstimate> GetPrices([FromBody] string data)
        {
            var auctions = MessagePack.LZ4MessagePackSerializer.Deserialize<IEnumerable<SaveAuction>>(Convert.FromBase64String(data));
            return auctions.Select(a =>
            {
                try
                {
                    return service.GetPrice(a);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "getting price for auction");
                }
                return new PriceEstimate();
            });
        }

        [Route("similar/{tag}/{auctionId}")]
        [HttpGet]
        public IEnumerable<KeyValuePair<AuctionKey, ReferenceAuctions>> SimilarKeys(string tag, string auctionId)
        {
            var firstKey = Search(tag, auctionId).FirstOrDefault();
            return SniperService.FindClosest(service.Lookups[tag].Lookup, firstKey);
        }

        [Route("search/{tag}/{itemId}")]
        [HttpGet]
        public IEnumerable<AuctionKey> Search(string tag, string itemId)
        {
            if (!long.TryParse(itemId, out long uid))
                uid = AuctionService.Instance.GetId(itemId);
            foreach (var bucket in service.Lookups[tag].Lookup)
            {
                foreach (var item in bucket.Value.References)
                {
                    if (item.AuctionId == uid)
                        yield return bucket.Key;
                }
            }
        }
        [Route("reassign")]
        [HttpPost]
        public async Task<List<Result>> Reassign(string tag, string value)
        {
            var toChange = Search(tag, value).ToList();
            var toCheck = service.Lookups[tag].Lookup.Where(l => toChange.Contains(l.Key)).SelectMany(l => l.Value.References.Select(r => (l.Key, r))).ToDictionary(r => r.r.AuctionId, r => r);
            List<SaveAuction> auctions = null;
            using (var context = new HypixelContext())
            {
                var uids = toCheck.Keys.Select(k => k).ToList();
                auctions = await context.Auctions.Where(a => uids.Contains(a.UId)).Include(a => a.NbtData).Include(a => a.Enchantments).ToListAsync();
            }

            var result = new List<Result>();
            foreach (var item in auctions)
            {
                var key = service.KeyFromSaveAuction(item);
                var actual = toCheck[item.UId];
                if (key == actual.Key)
                    continue;
                // yikes
                result.Add(new()
                {
                    New = key,
                    Old = actual.Key,
                    Reference = actual.r
                });

                service.Move(tag, actual.r.AuctionId, actual.Key, key);
            }
            return result;
        }

        public class Result
        {
            public AuctionKey Old { get; set; }
            public AuctionKey New { get; set; }
            public ReferencePrice Reference { get; set; }
        }

        /// <summary>
        /// Retrieve lookup references
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        [Route("export")]
        [ResponseCache(Duration = 3600 * 6, Location = ResponseCacheLocation.Any, NoStore = false)]
        public async Task<IEnumerable<float[]>> Export(int page = 0)
        {
            var batches = await Mapper.Instance.GetBatch(page, 30000);
            return batches.Select(p =>
            {
                var numbers = new List<float>();
                numbers.Add(p.HighestBid);
                numbers.Add(p.ItemId);
                numbers.Add(p.Rarity);
                //AddDate(numbers,  default(DateTime));
                numbers.Add(0f);
                numbers.Add(0f);
                numbers.Add(0f);
                numbers.Add(0f);
                AddDate(numbers, p.End);

                var enchants = Enum.GetValues<Coflnet.Sky.Core.Enchantment.EnchantmentType>().Select(t => 0f).ToArray();
                foreach (var item in p.Enchantments)
                {
                    enchants[item.Item1] = ((float)item.Item2) / 10;
                }
                numbers.AddRange(enchants);

                var nbt = new float[300];
                foreach (var item in p.NbtData)
                {
                    nbt[item.Item1] = (float)item.Item2 / int.MaxValue;
                }
                numbers.AddRange(nbt);


                return numbers.ToArray();
            });

            //return "ok";
        }

        private static void AddDate(List<float> numbers, DateTime date)
        {
            numbers.Add((float)date.TimeOfDay.TotalSeconds / 60 / 60 / 24);
            numbers.Add((float)date.DayOfWeek / 7);
            numbers.Add((float)date.DayOfYear / 365);
            numbers.Add((float)date.Day / 31);
        }
    }
}
