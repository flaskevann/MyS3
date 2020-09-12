using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.Serialization.Formatters.Binary;

using System.Windows.Forms;

using EncryptionAndHashingLibrary;

namespace MyS3.GUI
{
    public class SetupStore
    {
        private static readonly string SETUPS_FILE_PATH = "setups.bin";

        public static ImmutableDictionary<string, MyS3Setup> Entries { get { return setups.ToImmutableDictionary<string, MyS3Setup>(); } }
        private static Dictionary<string, MyS3Setup> setups = new Dictionary<string, MyS3Setup>();

        private static void Save()
        {
            if (setups.Count > 0)
            {
                // Serialize
                byte[] data;
                using (MemoryStream ms = new MemoryStream())
                {
                    BinaryFormatter binFormatter = new BinaryFormatter();
                    binFormatter.Serialize(ms, setups);
                    data = ms.ToArray();
                }

                Tools.WriteSettingsFile(SETUPS_FILE_PATH, data);
            }
        }

        private static void Load()
        {
            if (Tools.SettingsFileExists(SETUPS_FILE_PATH))
            {
                byte[] data = Tools.ReadSettingsFile(SETUPS_FILE_PATH);

                // Deserialize
                Dictionary<string, MyS3Setup> dic = new Dictionary<string, MyS3Setup>();
                using (MemoryStream ms = new MemoryStream(data))
                {
                    BinaryFormatter binFormatter = new BinaryFormatter();
                    setups = (Dictionary<string, MyS3Setup>)binFormatter.Deserialize(ms);
                }
            }
        }

        static SetupStore()
        {
            Load();
        }

        // ---

        public static void Add(MyS3Setup setup)
        {
            if (setups.ContainsKey(setup.Bucket))
                setups.Remove(setup.Bucket);
            setups.Add(setup.Bucket, setup);

            Save();
        }

        public static void Remove(string bucket)
        {
            if (setups.ContainsKey(bucket))
            {
                setups.Remove(bucket);

                Save();
            }
        }

        public static void Remove(MyS3Setup setup)
        {
            if (setups.ContainsKey(setup.Bucket))
            {
                setups.Remove(setup.Bucket);

                Save();
            }
        }
    }
}
