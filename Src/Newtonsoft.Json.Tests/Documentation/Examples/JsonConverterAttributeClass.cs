﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Converters;

namespace Newtonsoft.Json.Tests.Documentation.Examples
{
  public class JsonConverterAttributeClass
  {
    public class UserConverter : JsonConverter
    {
      public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
      {
        User user = (User) value;

        writer.WriteValue(user.UserName);
      }

      public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
      {
        User user = new User();
        user.UserName = (string)reader.Value;

        return user;
      }

      public override bool CanConvert(Type objectType)
      {
        return objectType == typeof (User);
      }
    }

    [JsonConverter(typeof(UserConverter))]
    public class User
    {
      public string UserName { get; set; }
    }

    public void Example()
    {
      User user = new User
      {
        UserName = @"domain\username"
      };

      string json = JsonConvert.SerializeObject(user, Formatting.Indented);

      Console.WriteLine(json);
      // "domain\\username"
    }
  }
}
