using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace FemboyChanger.Core
{
    public class SkinData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = ""; // Localized
        public int WeaponId { get; set; }
        public string WeaponName { get; set; } = ""; // Localized
        public int PaintIndex { get; set; }
        public string ImageUrl { get; set; } = "";
        
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
                            ImageUrl = item["image"]?.ToString()
                        });
                    } catch { } // skip parsing errors for individual skins
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to load skins: " + ex.Message);
            }
        }

        public static IEnumerable<IGrouping<int, SkinData>> GetGroupedSkins()
        {
            return Skins.GroupBy(s => s.WeaponId);
        }
    }
}