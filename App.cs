﻿/*
 * VitaDB - Vita DataBase Updater © 2017 VitaSmith
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text.RegularExpressions;

using static VitaDB.Utilities;

namespace VitaDB
{
    public class App
    {
        public string TITLE_ID { get; set; }
        public string NAME { get; set; }
        public string ALT_NAME { get; set; }
        [Key]
        public string CONTENT_ID { get; set; }
        public string PARENT_ID { get; set; }
        public int? CATEGORY { get; set; }
        public UInt32? PKG_ID { get; set; }
        public string ZRIF { get; set; }
        public string COMMENTS { get; set; }
        public UInt16 FLAGS { get; set; }

        [NotMapped]
        public string PKG_URL { get; set; }

        static private UInt32 count = 0;

        /// <summary>
        /// Set a flag or a set of flags.
        /// </summary>
        /// <param name="db">The database context.</param>
        /// <param name="flag_names">One or more names of flags to set.</param>
        public void SetFlag(Database db, params string[] flag_names)
        {
            for (int i = 0; i < flag_names.Length; i++)
                this.FLAGS |= (UInt16)db.Flag[flag_names[i]];
        }

        /// <summary>
        /// Set an attribute to read-only.
        /// </summary>
        /// <param name="db">The database context.</param>
        /// <param name="attr_names">The names of one or more attrinbutes to set to read-only.</param>
        public void SetReadOnly(Database db, params string[] attr_names)
        {
            for (int i = 0; i < attr_names.Length; i++)
                this.FLAGS |= (UInt16)db.Flag[attr_names[i] + "_RO"];
        }

        /// <summary>
        /// Insert or update a new application entry into the Apps database.
        /// This method preserves attributes that have been flag to read-only.
        /// Note: This method only applies changes to the databse every 100 records.
        /// </summary>
        /// <param name="db">The database context.</param>
        public void Upsert(Database db)
        {
            var app = db.Apps.Find(CONTENT_ID);
            if (app == null)
            {
                db.Apps.Add(this);
            }
            else
            {
                var org_app = db.Apps
                    .AsNoTracking()
                    .FirstOrDefault(x => x.CONTENT_ID == this.CONTENT_ID);
                if (org_app == null)
                {
                    // Changes need to be applied
                    db.SaveChanges();
                    org_app = db.Apps
                        .AsNoTracking()
                        .FirstOrDefault(x => x.CONTENT_ID == this.CONTENT_ID);
                    if (org_app == null)
                        throw new ApplicationException("Tracked App found, but database changes were not applied.");
                }
                var entry = db.Entry(this);

                foreach (var attr in typeof(App).GetProperties())
                {
                    if ((attr.Name == nameof(PKG_URL)) || (attr.Name == nameof(CONTENT_ID)))
                        continue;

                    // Manually Check for values that have been modified
                    var new_value = attr.GetValue(this, null);
                    var org_value = attr.GetValue(org_app, null);
                    if ((new_value == null) || new_value.Equals(org_value))
                    {
                        entry.Property(attr.Name).IsModified = false;
                        continue;
                    }

                    // Set modified attribute according to the read-only flags
                    if (db.Flag.ContainsKey(attr.Name + "_RO"))
                    {
                        entry.Property(attr.Name).IsModified =
                            ((org_app.FLAGS & db.Flag[attr.Name + "_RO"]) == 0);
                    }
                    else
                    {
                        entry.Property(attr.Name).IsModified = true;
                    }
                }

                // Flags can only be added in this method, never removed
                if (this.FLAGS != org_app.FLAGS)
                {
                    this.FLAGS |= org_app.FLAGS;
                    entry.Property(nameof(FLAGS)).IsModified = true;
                }
            }
            if (++count % 100 == 0)
                db.SaveChanges();
        }

        /// <summary>
        /// Validate that TITLE_ID matches the expected format.
        /// </summary>
        /// <param name="title_id">The TITLE_ID string.</param>
        /// <returns>true if TITLE_ID is valid, false otherwise.</returns>
        public static bool ValidateTitleID(string title_id)
        {
            Regex regexp = new Regex(@"^[A-Z]{4}\d{5}$");
            if (title_id == null)
                return false;
            return regexp.IsMatch(title_id);
        }

        /// <summary>
        /// Validate that CONTENT_ID matches the expected format.
        /// </summary>
        /// <param name="title_id">The CONTENT_ID string.</param>
        /// <returns>true if CONTENT_ID is valid, false otherwise.</returns>
        public static bool ValidateContentID(string content_id)
        {
            Regex regexp = new Regex(@"^[A-Z\?]{2}[\d\?]{4}-[A-Z]{4}\d{5}_[\d\?]{2}-[A-Z0-9_\?]{16}$");
            if (content_id == null)
                return false;
            return regexp.IsMatch(content_id);
        }

        /// <summary>
        /// Update an App entry by querying Chihiro.
        /// </summary>
        /// <param name="db">The database context.</param>
        /// <param name="app">The application entry to update or create.</param>
        /// <param name="lang">(Optional) The language settings to use when querying Chihiro.</param>
        /// <param name="add_lang">(Optional) If true, also add lang to the COMMENTS field.</param>
        public void UpdateFromChihiro(Database db, string lang = null, bool add_lang = false)
        {
            var data = Chihiro.GetData(CONTENT_ID, lang);
            if (data == null)
                return;

            if (TITLE_ID.StartsWith('P'))
            {
                // Straight Vita title
                Console.WriteLine($"{TITLE_ID}: {data.name}");
                NAME = data.name;
                CATEGORY = db.Category[data.top_category];
                // Data we get from Chihiro is final
                SetReadOnly(db, nameof(App.NAME), nameof(App.CATEGORY));
                if ((lang != null) && (add_lang))
                {
                    COMMENTS = lang;
                    SetReadOnly(db, nameof(App.COMMENTS));
                }
                Upsert(db);

                // Update addons
                foreach (var link in Nullable(data.links))
                {
                    // May get mixed DLC content
                    if ((link.id[7] != 'P') && (link.id[7] != 'V'))
                        continue;
                    Console.WriteLine($"* {link.id}: {link.top_category}");
                    var dlc = db.Apps.Find(link.id);
                    if (dlc == null)
                    {
                        dlc = new App
                        {
                            NAME = link.name,
                            TITLE_ID = link.id.Substring(7, 9),
                            CONTENT_ID = link.id,
                            PARENT_ID = CONTENT_ID,
                            CATEGORY = db.Category[link.top_category],
                        };
                    }
                    else
                    {
                        dlc.NAME = link.name;
                        dlc.TITLE_ID = link.id.Substring(7, 9);
                        if (dlc.PARENT_ID == null)
                            dlc.PARENT_ID = CONTENT_ID;
                        else if (!dlc.PARENT_ID.Contains(CONTENT_ID))
                            dlc.PARENT_ID += " " + CONTENT_ID;
                        dlc.CATEGORY = db.Category[link.top_category];
                    }
                    dlc.SetReadOnly(db, nameof(App.NAME), nameof(App.CATEGORY));
                    if ((lang != null) && (add_lang))
                    {
                        dlc.COMMENTS = lang;
                        dlc.SetReadOnly(db, nameof(App.COMMENTS));
                    }
                    dlc.Upsert(db);
                }
            }
            else
            {
                // PS3/PS4/Vita bundle
                Console.WriteLine($"{TITLE_ID} (BUNDLE): {data.name}");
                if (data.default_sku != null)
                {
                    foreach (var ent in Nullable(data.default_sku.entitlements))
                    {
                        if (ent.id[7] != 'P')
                            continue;
                        Console.WriteLine($"* {ent.id}: {ent.name}");
                        var app = db.Apps.Find(ent.id);
                        if (app == null)
                        {
                            app = new App
                            {
                                NAME = ent.name,
                                TITLE_ID = ent.id.Substring(7, 9),
                                CONTENT_ID = ent.id,
                                PARENT_ID = CONTENT_ID,
                                CATEGORY = CATEGORY,
                            };
                        }
                        else
                        {
                            app.NAME = ent.name;
                            app.TITLE_ID = ent.id.Substring(7, 9);
                            if (app.PARENT_ID == null)
                                app.PARENT_ID = CONTENT_ID;
                            app.CATEGORY = CATEGORY;
                        }
                        app.SetReadOnly(db, nameof(App.NAME), nameof(App.CATEGORY));
                        app.Upsert(db);
                    }
                }
            }
        }
    }

    // DB entries for App.CATEGORY
    public class Category
    {
        public string NAME { get; set; }
        [Key]
        public int VALUE { get; set; }
    }

    // DB entries for App.FLAGS
    public class Flag
    {
        public string NAME { get; set; }
        [Key]
        public int VALUE { get; set; }
    }
}
