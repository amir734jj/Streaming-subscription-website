﻿using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Models.Interfaces;

namespace Models.Models
{
    /// <summary>
    /// Website user model
    /// </summary>
    public class User : IdentityUser<int>, IEntity
    {
        public string Name { get; set; }

        public List<Stream> Streams { get; set; }

        public object Obfuscate()
        {
            const string pattern = @"(?<=[\w]{1})[\w-\._\+%]*(?=[\w]{1}@)";

            var obfuscatedEmail = Regex.Replace(string.Empty, pattern, m => new string('*', m.Length));
            
            return new {Email = obfuscatedEmail, Name};
        }
    }
}