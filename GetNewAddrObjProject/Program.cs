using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Net.WebRequestMethods;
using File = System.IO.File;

class Program
{
    static async Task Main(string[] args)
    {
        string serviceUrl = "https://fias.nalog.ru/WebServices/Public/DownloadService.asmx/GetLastDownloadFileInfo";
        string zipFilePath = "latest_update.zip";
        string extractPath = "extracted_files";
        string fileAddrPattern = "AS_ADDR_OBJ_2*.xml";
        string versionFilePath = Path.Combine(extractPath, "version.txt");
        string fileLevelPattern = "AS_OBJECT_LEVELS*";
        string reportFilePath = "report.txt";

        var levelObjectDictionary = new Dictionary<int, string>();

        try
        {
            // Шаг 1: Получить URL последнего пакета изменений
            string downloadUrl = await GetLatestDownloadUrl(serviceUrl);

            // Шаг 2: Скачать zip-архив
            await DownloadZipFile(downloadUrl, zipFilePath);

            // Шаг 3: Разархивировать zip-архив
            ExtractZipFile(zipFilePath, extractPath);

            var directories = Directory.GetDirectories(extractPath);
            // Шаг 4: Получить значения из файлов AS_ADDR_OBJ
            foreach(var directory in directories)
            {
                var files = Directory.GetFiles(directory, fileAddrPattern);
                foreach(var file in files)
                {
                    ParseAddrObjFile(file, levelObjectDictionary);
                }
            }

            // Шаг 5: Получить дату изменений
            string date = ReadDateFromVersionFile(versionFilePath);

            // Шаг 6: Получить необходимые названия уровней
            var levelNames = ParseObjectLevelsFile(extractPath, fileLevelPattern);

            // Шаг 7: Сформировать итоговый отчёт 
            GenerateReport(reportFilePath, date, levelObjectDictionary, levelNames);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Возникло исключение: {ex.Message}");
        }
    }

    static async Task<string> GetLatestDownloadUrl(string serviceUrl)
    {
        using (HttpClient client = new HttpClient())
        {
            HttpResponseMessage response = await client.GetAsync(serviceUrl);
            response.EnsureSuccessStatusCode();

            string responseContent = await response.Content.ReadAsStringAsync();

            // Parse the XML response to get the download URL
            XDocument xmlDoc = XDocument.Parse(responseContent);
            XNamespace ns = xmlDoc.Root.GetDefaultNamespace();
            XElement garXmlDeltaUrlElement = xmlDoc.Descendants(ns + "GarXMLDeltaURL").FirstOrDefault();

            if (garXmlDeltaUrlElement == null)
            {
                throw new Exception("GarXMLDeltaURL не найден в ответе на запрос");
            }

            return garXmlDeltaUrlElement.Value;
        }
    }

    static async Task DownloadZipFile(string downloadUrl, string zipFilePath)
    {
        using (HttpClient client = new HttpClient())
        {
            HttpResponseMessage response = await client.GetAsync(downloadUrl);
            response.EnsureSuccessStatusCode();

            using (FileStream fs = new FileStream(zipFilePath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }
        }
    }

    static void ExtractZipFile(string zipFilePath, string extractPath)
    {
        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        ZipFile.ExtractToDirectory(zipFilePath, extractPath);
    }

    static void ParseAddrObjFile(string filePath, Dictionary<int, string> dictionary)
    {
        try
        {
            XDocument xmlDoc = XDocument.Load(filePath);
            var objects = xmlDoc.Descendants("OBJECT")
                                .Where(o => (int)o.Attribute("OPERTYPEID") == 10 ||
                                            (int)o.Attribute("OPERTYPEID") == 43 ||
                                            (int)o.Attribute("OPERTYPEID") == 61)
                                .Where(o => (int)o.Attribute("ISACTIVE") == 1);

            foreach (var obj in objects)
            {
                int level = (int)obj.Attribute("LEVEL");
                string typeName = (string)obj.Attribute("TYPENAME");
                string name = (string)obj.Attribute("NAME");
                string fullName = $"{typeName},{name}";

                if (dictionary.ContainsKey(level))
                {
                    dictionary[level] += "|" + fullName;
                }
                else
                {
                    dictionary[level] = fullName;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to parse XML file {filePath}: {ex.Message}");
        }
    }

    static string ReadDateFromVersionFile(string filePath)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);
            var dateTockens = lines[0].Trim().Split('.');
            return $"{dateTockens[2]}.{dateTockens[1]}.{dateTockens[0]}";
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка чтения даты из файла версии: {ex.Message}");
        }
    }

    static Dictionary<int, string> ParseObjectLevelsFile(string extractPath, string filePattern)
    {
        var levelNames = new Dictionary<int, string>();

        try
        {
            var filePath = Directory.GetFiles(extractPath, filePattern).FirstOrDefault();
            XDocument xmlDoc = XDocument.Load(filePath);
            var levels = xmlDoc.Descendants("OBJECTLEVEL");

            foreach (var level in levels)
            {
                int levelNumber = (int)level.Attribute("LEVEL");
                string levelName = (string)level.Attribute("NAME");
                levelNames[levelNumber] = levelName;
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка парсинга файла AS_OBJECT_LEVELS: {ex.Message}");
        }

        return levelNames;
    }

    static void GenerateReport(string filePath, string date, Dictionary<int, string> data, Dictionary<int, string> levelNames)
    {
        try
        {
            using (StreamWriter writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                // Написать шапку
                writer.WriteLine($"Отчёт по добавленным адресным объектам за {date}");
                writer.WriteLine(new string('-', 80));
                // Написать таблицы
                foreach (var entry in data)
                {
                    int level = entry.Key;
                    string levelName = levelNames.ContainsKey(level) ? levelNames[level] : $"Уровень {level}";

                    writer.WriteLine($"\n{levelName}");

                    var rows = entry.Value.Split('|')
                                          .Select(row => row.Split(','))
                                          .OrderBy(row => row[1]) // Сортировать по второму столбцу
                                          .ToList();

                    writer.WriteLine(new string('-', 80));
                    writer.WriteLine("Тип объекта\tНаименование");

                    foreach (var row in rows)
                    {
                        writer.WriteLine($"{row[0]}\t\t{row[1]}");
                    }
                    writer.WriteLine(new string('-', 80));
                }
                writer.Flush();
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка формирования отчёта: {ex.Message}");
        }
    }
}