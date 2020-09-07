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
        private static readonly string APP_DATA_DIRECTORY_PATH = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + @"\" + "MyS3" + @"\";
        private static readonly string SETUPS_FILE_PATH = APP_DATA_DIRECTORY_PATH + "setups.bin";

        public static ImmutableDictionary<string, MyS3Setup> Entries { get { return setups.ToImmutableDictionary<string, MyS3Setup>(); } }
        private static Dictionary<string, MyS3Setup> setups = new Dictionary<string, MyS3Setup>();

        private static void SaveToFile()
        {
            // Setup
            if (!Directory.Exists(APP_DATA_DIRECTORY_PATH)) Directory.CreateDirectory(APP_DATA_DIRECTORY_PATH);

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

                // Encrypt and write to file
                byte[] encryptedData = AesEncryptionWrapper.EncryptWithGCM(
                    data,
                    EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, "MyS3")
                );
                File.WriteAllBytes(SETUPS_FILE_PATH, encryptedData);
            }
            else
            {
                if (File.Exists(SETUPS_FILE_PATH))
                    File.Delete(SETUPS_FILE_PATH);
            }
        }

        private static void LoadFromFile()
        {
            // Setup
            if (!Directory.Exists(APP_DATA_DIRECTORY_PATH)) Directory.CreateDirectory(APP_DATA_DIRECTORY_PATH);

            try
            {
                if (File.Exists(SETUPS_FILE_PATH))
                {
                    // Read from file and decrypt
                    byte[] encryptedData = File.ReadAllBytes(SETUPS_FILE_PATH);
                    byte[] data = AesEncryptionWrapper.DecryptForGCM(
                        encryptedData,
                        EncryptionAndHashingLibrary.Tools.GetPasswordAsEncryptionKey(AesEncryptionWrapper.GCM_KEY_SIZE, "MyS3")
                    );

                    // Deserialize
                    Dictionary<string, MyS3Setup> dic = new Dictionary<string, MyS3Setup>();
                    using (MemoryStream ms = new MemoryStream(data))
                    {
                        BinaryFormatter binFormatter = new BinaryFormatter();
                        setups = (Dictionary<string, MyS3Setup>)binFormatter.Deserialize(ms);
                    }
                }
            }
            catch (Exception) { }
        }

        static SetupStore()
        {
            LoadFromFile();
        }

        // ---

        public static void Add(MyS3Setup setup)
        {
            if (setups.ContainsKey(setup.Bucket))
                setups.Remove(setup.Bucket);
            setups.Add(setup.Bucket, setup);

            SaveToFile();
        }

        public static void Remove(MyS3Setup setup)
        {
            if (setups.ContainsKey(setup.Bucket))
            {
                setups.Remove(setup.Bucket);

                SaveToFile();
            }
        }
    }
}
