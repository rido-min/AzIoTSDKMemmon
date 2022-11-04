using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzIoTSDKMemmon
{
    internal static class TwinCollectionExtension
    {
        public static T GetPropertyValue<T>(this TwinCollection collection, string propertyName)
        {
            T result = default(T);
            if (collection.Contains(propertyName))
            {
                var propertyJson = collection[propertyName] as JObject;
                if (propertyJson != null)
                {
                    if (propertyJson.ContainsKey("value"))
                    {
                        var propertyValue = propertyJson["value"];
                        result = propertyValue!.Value<T>()!;
                    }
                }
                else
                {
                    result = collection[propertyName].Value;
                }
            }
            return result;
        }
    }
}
