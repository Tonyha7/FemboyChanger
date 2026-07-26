using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FemboyChanger.Core
{
    public enum SkinKind
    {
        Weapon,
        Knife,
        Glove,
    }

    public class SkinData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = ""; // Localized
        public int WeaponId { get; set; }

        /// <summary>
        /// Taken from the item's category rather than guessed from the id range. Broken Fang
        /// Gloves are item 4725, well outside the 5027-5035 block gloves otherwise occupy, so a
        /// numeric range silently treated them as an ordinary weapon and they never applied.
        /// </summary>
        public SkinKind Kind { get; set; } = SkinKind.Weapon;
        public string WeaponName { get; set; } = ""; // Localized
        public int PaintIndex { get; set; }
        public string ImageUrl { get; set; } = "";

        /// <summary>
        /// True when the paint kit was authored for the pre-2018 mesh. client.dll reads the same
        /// flag as <c>use_legacy_model</c> on the paint kit definition; without switching the
        /// weapon to its legacy mesh group the texture is mapped onto the wrong UVs.
        /// </summary>
        public bool LegacyModel { get; set; }
        
        public Task<Avalonia.Media.Imaging.Bitmap?> ImageTask => ImageHelper.LoadImageAsync(ImageUrl);
    }

    public static class ImageHelper
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Avalonia.Media.Imaging.Bitmap> Cache = new();
        private static readonly HttpClient _httpClient = new HttpClient();

        public static async Task<Avalonia.Media.Imaging.Bitmap?> LoadImageAsync(string url)
        {
            if (string.IsNullOrEmpty(url)) return null;
            if (Cache.TryGetValue(url, out var bitmap)) return bitmap;
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                Cache.TryAdd(url, bmp);
                return bmp;
            }
            catch { return null; }
        }
    }

    public static class DataProvider
    {
        public static List<SkinData> Skins = new List<SkinData>();
        
        public static async Task LoadSkinsAsync(bool isChinese = false)
        {
            try
            {
                using var client = new HttpClient();
                string url = isChinese 
                    ? "https://ob.tonyha7.com/skins_cn.json" 
                    : "https://ob.tonyha7.com/skins.json";
                    
                string json = await client.GetStringAsync(url);
                var jArray = JArray.Parse(json);
                
                Skins.Clear();
                foreach (var item in jArray)
                {
                    try {
                        var paintIndexToken = item["paint_index"];
                        if (paintIndexToken == null) continue;
                        
                        Skins.Add(new SkinData
                        {
                            Id = item["id"]?.ToString(),
                            Name = item["name"]?.ToString(),
                            WeaponId = int.Parse(item["weapon"]["weapon_id"].ToString()),
                            WeaponName = item["weapon"]["name"]?.ToString(),
                            PaintIndex = int.Parse(paintIndexToken.ToString()),
                            ImageUrl = item["image"]?.ToString(),
                            LegacyModel = (bool?)item["legacy_model"] ?? false,
                            Kind = ClassifyCategory(item["category"]?["id"]?.ToString())
                        });
                    } catch { } // skip parsing errors for individual skins
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load skins: " + ex.Message);
            }
        }

        private static SkinKind ClassifyCategory(string? categoryId) => categoryId switch
        {
            "sfui_invpanel_filter_gloves" => SkinKind.Glove,
            "sfui_invpanel_filter_melee" => SkinKind.Knife,
            _ => SkinKind.Weapon,
        };

        public static IEnumerable<IGrouping<int, SkinData>> GetGroupedSkins()
        {
            return Skins.GroupBy(s => s.WeaponId);
        }
    }
}