using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using DotNetEnv;
using InsuranceBot;
using System.Text.Json;
using Mindee;
using Mindee.Input;
using Mindee.Product.InternationalId;
using Mindee.Parsing.Common;
using System.Text;
using Mindee.Http;
using Telegram.Bot.Types.ReplyMarkups;
using iTextSharp.text.pdf;
using iTextSharp.text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

class Program
{        
    static Dictionary<long,String> UserStates = new();
    static DataBaseService dataBaseService;
    private static readonly string botToken = Env.GetString("BOT_TOKEN");
    private static readonly string dbConnection = Env.GetString("DB_CONNECTION");
    private static readonly string mistralApiKey = Env.GetString("MISTRAL_API_KEY");
    private static readonly string mistralUrl = "https://api.mistral.ai/v1/chat/completions";
    private static readonly HttpClient httpClient = new HttpClient();

    static async Task Main(string[] args)
    {
       System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        Env.Load();

        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/", () => "Bot is running!");

        var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";

        dataBaseService = new DataBaseService(dbConnection);

        if (string.IsNullOrEmpty(botToken))
        {
            Console.WriteLine("Токен не знайдено! Перевір .env файл.");
        }
        else
        {
            var botClient = new TelegramBotClient(botToken);
            botClient.StartReceiving(
                HandleUpdate,
                HandleError
            );
            Console.WriteLine("Бот запущено.");
        }

        // Запускаємо веб-сервер
        app.Run($"http://0.0.0.0:{port}");
    }
    static async Task HandleUpdate(ITelegramBotClient botClient, Update update, CancellationToken token)
    {
        try
        {
            if (update.Message is { } message)
            {
                Console.WriteLine($"Отримано повідомлення від {message.Chat.Id}. Тип: {message.Type}");

                long userId = message.Chat.Id;

                if (message.Type == MessageType.Text)
                {


                    Console.WriteLine($"Текст повідомлення: {message.Text}");

                    if (message.Text == "/start")
                    {
                        UserStates[userId] = "awaiting_passport";
                        await botClient.SendMessage(
                            chatId: userId,
                            text: "Привіт! Я бот для покупки страхового полісу на автомобіль.\n" +
                                  "Можеш придбати його через мене, тільки надішли мені фото свого паспорта.",
                            parseMode: ParseMode.Markdown,
                            cancellationToken: token
                        );
                        Console.WriteLine($"Користувачу {userId} надіслано інструкцію.");
                        return;
                    }
                    else
                    {
                        string response = await GetMistralResponse(message.Text);
                        await botClient.SendMessage(message.Chat.Id, response, cancellationToken: token);
                        return;
                    }

                }

                if (message.Type == MessageType.Photo)
                {
                    Console.WriteLine($"Отримано фото від {userId}");

                    if (UserStates.TryGetValue(userId, out var state))
                    {
                        if (state == "awaiting_passport")
                        {
                            var photo = message.Photo.Last();
                            var file = await botClient.GetFile(photo.FileId, cancellationToken: token);

                            using var ms = new MemoryStream();
                            await botClient.DownloadFile(file.FilePath, ms, cancellationToken: token);
                            var imageData = ms.ToArray();

                            Console.WriteLine($"Отримано паспорт користувача {userId}, розмір: {imageData.Length} байт");
                            var (success, document, error) = await CallMindeeApiPassportAsync(imageData);

                            if (!success)
                            {
                                await botClient.SendMessage(userId, $"Помилка розпізнавання документа: {error}", cancellationToken: token);
                                return;
                            }

                            var prediction = document.Inference.Prediction;

                            string firstName = prediction.GivenNames != null
                                ? string.Join(" ", prediction.GivenNames.Select(n => n.Value))
                                : "невідомо";

                            string lastName = prediction.Surnames != null
                                ? string.Join(" ", prediction.Surnames.Select(n => n.Value))
                                : "невідомо";

                            string docNumber = prediction.DocumentNumber?.Value ?? "невідомо";

                            string extractedInfo = $"Паспортні дані:\nІм'я: {firstName}\nПрізвище: {lastName}\nНомер документа: {docNumber}\n\n" +
                                                   "Підтвердіть, будь ласка, чи все правильно?";

                            var keyboard = new InlineKeyboardMarkup(new[]
                            {
                            new []
                            {
                               InlineKeyboardButton.WithCallbackData("✅ Так", "confirm_yes"),
                               InlineKeyboardButton.WithCallbackData("❌ Ні", "confirm_no"),
                            }
                            });

                            await botClient.SendMessage(chatId: userId, text: extractedInfo, replyMarkup: keyboard, cancellationToken: token);
                            UserStates[userId] = "awaiting_passport_confirmation";

                            await dataBaseService.SaveDocumentToDataBase(userId, "passport", imageData);

                            Console.WriteLine("Документ збережено успішно в базу даних");
                        }
                        else if (state == "awaiting_car_document")
                        {
                            var photo = message.Photo.Last();
                            var file = await botClient.GetFile(photo.FileId, cancellationToken: token);

                            using var ms = new MemoryStream();
                            await botClient.DownloadFile(file.FilePath, ms, cancellationToken: token);
                            var imageData = ms.ToArray();

                            Console.WriteLine($"Отримано техпаспорт від {userId}, розмір: {imageData.Length} байт");

                            var (success, document, error) = await CallMindeeApiWehicleRegistrationAsync(imageData);

                            if (!success)
                            {
                                await botClient.SendMessage(userId, $"Помилка розпізнавання: {error}", cancellationToken: token);
                                return;
                            }

                            var prediction = document.Inference.Prediction;

                            string registrationPlate = prediction.Fields.ContainsKey("registration_number") ?
                                prediction.Fields["registration_number"].ToString() : "невідомо";

                            string vehicleMake = prediction.Fields.ContainsKey("brand") ?
                                prediction.Fields["brand"].ToString() : "невідомо";

                            string vehicleModel = prediction.Fields.ContainsKey("model") ?
                                prediction.Fields["model"].ToString() : "невідомо";

                            string vin = prediction.Fields.ContainsKey("vin") ?
                                prediction.Fields["vin"].ToString() : "невідомо";

                            string year = prediction.Fields.ContainsKey("year") ?
                                prediction.Fields["year"].ToString() : "невідомо";

                            string extractedInfo = $"Дані з техпаспорту:\nНомер: {registrationPlate}\nМарка: {vehicleMake}\nМодель: {vehicleModel}\nVIN: {vin}\nРік випуску: {year}\n\n" +
                                $"Підтвердіть, будь ласка, чи все правильно?";

                            var keyboard = new InlineKeyboardMarkup(new[]
                            {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("✅ Так", "confirm_car_yes"),
                                InlineKeyboardButton.WithCallbackData("❌ Ні", "confirm_car_no"),
                            }
                            });

                            await botClient.SendMessage(chatId: userId, text: extractedInfo, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: token);

                            UserStates[userId] = "awaiting_car_document_confirmation";
                            await dataBaseService.SaveDocumentToDataBase(userId, "car_doc", imageData);
                        }
                    }

                }
            }

            if (update.CallbackQuery is { } callback)
            {
                long userId = callback.From.Id;
                string data = callback.Data;

                if (UserStates.TryGetValue(userId, out var state) && state == "awaiting_passport_confirmation")
                {
                    if (data == "confirm_yes")
                    {
                        await botClient.DeleteMessage(chatId: callback.Message.Chat.Id, messageId: callback.Message.MessageId, cancellationToken: token); 
                        await botClient.AnswerCallbackQuery(callback.Id, "Дані підтверджено!");
                        await botClient.SendMessage(userId, "Добре! Тепер, будь ласка, надішліть фото техпаспорта автомобіля.", cancellationToken: token);
                        UserStates[userId] = "awaiting_car_document";
                    }
                    else if (data == "confirm_no")
                    {
                        await botClient.DeleteMessage(chatId: callback.Message.Chat.Id, messageId: callback.Message.MessageId, cancellationToken: token);
                        await botClient.AnswerCallbackQuery(callback.Id, "Дані не підтверджено.");
                        await botClient.SendMessage(userId, "Будь ласка, надішліть чіткіше фото документа.", cancellationToken: token);
                        UserStates[userId] = "awaiting_passport";
                    }
                }
                else if (state == "awaiting_car_document_confirmation")
                {
                    if (data == "confirm_car_yes")
                    {
                        await botClient.AnswerCallbackQuery(callback.Id, "Дані підтверджено!");
                        var keyboard = new InlineKeyboardMarkup(new[]
                        {
                            new[]
                            {
                                InlineKeyboardButton.WithCallbackData("Придбати", "buy_policy"),
                                InlineKeyboardButton.WithCallbackData("Відмовитись", "decline_policy"),
                            }
                        });
                        await botClient.DeleteMessage(chatId: callback.Message.Chat.Id, messageId: callback.Message.MessageId, cancellationToken: token);
                        await botClient.SendMessage(userId,"Добре! Поліс автострахування вартує 100$. Бажаєте придбати?",replyMarkup: keyboard,cancellationToken: token);
                        UserStates[userId] = "awaiting_policy_decision";
                    }
                    else if (data == "confirm_car_no")
                    {
                        await botClient.DeleteMessage(chatId: callback.Message.Chat.Id, messageId: callback.Message.MessageId, cancellationToken: token);
                        await botClient.AnswerCallbackQuery(callback.Id, "Дані не підтверджено.");
                        await botClient.SendMessage(userId, "Будь ласка, надішліть чіткіше фото документа.", cancellationToken: token);
                        UserStates[userId] = "awaiting_car_document";
                    }
                    return;
                }
                if (state == "awaiting_policy_decision")
                {
                    if (data == "buy_policy")
                    {
                        await botClient.DeleteMessage(chatId: callback.Message.Chat.Id,messageId: callback.Message.MessageId,cancellationToken: token); await botClient.AnswerCallbackQuery(callback.Id, "Вітаю! Ви придбали страховий поліс!");
                        await botClient.SendMessage(userId, "Дякуємо за покупку! Ваш страховий поліс оформлено.", cancellationToken: token);
                        await botClient.SendMessage(userId, "Генерую страховий поліс, зачекайте...");

                        string insuranceText = await GenerateInsuranceTextFromMistral();
                        Console.WriteLine(insuranceText);
                        if (string.IsNullOrEmpty(insuranceText))
                        {
                            await botClient.SendMessage(userId, "Не вдалося згенерувати текст страховки.");
                            return;
                        }

                        var pdfStream = GeneratePdfFromText(insuranceText);

                        var inputFile = new InputFileStream(pdfStream, "insurance_policy.pdf");
                        await botClient.SendDocument(userId, inputFile, "Ваш страховий поліс");


                        pdfStream.Dispose();
                    }
                    else if (data == "decline_policy")
                    {
                        await botClient.AnswerCallbackQuery(callback.Id, "Нажаль, поліс не придбано.");
                        string response = await GetMistralResponse("Дай коротку відповідь що нажаль наразі така ціна (100$) на страховий поліс і якщо клієнт передумає то ти завжди радий допомогти в придбані");
                        await botClient.SendMessage(userId, response, cancellationToken: token);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Помилка під час обробки повідомлення: {ex.Message}");
        }
    }


    static Task HandleError(ITelegramBotClient botClient, Exception exception, CancellationToken token)
    {
        Console.WriteLine($"Сталася помилка: {exception.Message}");
        return Task.CompletedTask;
    }
    private static async Task<string> GetMistralResponse(string question)
    {
        var requestData = new
        {
            model = "mistral-tiny",
            messages = new[]
            {
                new { role = "system", content = "Ти бот для купівлі страховки на авто, ціна 100$, ввійди в роль, та допамагай тільки на питання які стосуються автострахування, на інші питання ввічливо відповідай що не можеш тільки коли питання не стосується автострахування, також якщо тебе питають як купити страховку то користувач повинен написати /start (не надавай більше ніяких інструкцій) в чат і далі всі подальші інструкції будуть в чаті. Відповідай тільки українською, коротко та схоже до людського спілкування." },
                new { role = "user", content = question }
            }
        };

        string jsonRequest = JsonSerializer.Serialize(requestData);
        var requestContent = new StringContent(jsonRequest, Encoding.UTF8, "application/json");

        httpClient.DefaultRequestHeaders.Clear();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {mistralApiKey}");

        HttpResponseMessage response = await httpClient.PostAsync(mistralUrl, requestContent);
        string jsonResponse = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(jsonResponse);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
    }

    static async Task<(bool Success, Document<InternationalIdV2> Document, string ErrorMessage)> CallMindeeApiPassportAsync(byte[] imageData)
    {
        try
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            await File.WriteAllBytesAsync(tempFilePath, imageData);

            string mindeeApiKey = Env.GetString("MINDEE_API_KEY");
            var mindeeClient = new MindeeClient(mindeeApiKey);

            var inputSource = new LocalInputSource(tempFilePath);
            var response = await mindeeClient.EnqueueAndParseAsync<InternationalIdV2>(inputSource);

            File.Delete(tempFilePath);

            return (true, response.Document, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Сталася помилка: {ex.Message}");
        }
    }
    static async Task<(bool Success, Document<Mindee.Product.Generated.GeneratedV1> Document, string ErrorMessage)> CallMindeeApiWehicleRegistrationAsync(byte[] imageData)
    {
        try
        {
            string tempFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".jpg");
            await File.WriteAllBytesAsync(tempFilePath, imageData);

            string mindeeApiKey = Env.GetString("MINDEE_API_KEY");
            var mindeeClient = new MindeeClient(mindeeApiKey);
            CustomEndpoint endpoint = new CustomEndpoint(
                endpointName: "test",
                accountName: "Shmatoq2",
                version: "1"
            );

            var inputSource = new LocalInputSource(tempFilePath);
            

            var response = await mindeeClient.EnqueueAndParseAsync<Mindee.Product.Generated.GeneratedV1>(inputSource, endpoint);
            File.Delete(tempFilePath);

            return (true, response.Document, null);
        }
        catch (Exception ex)
        {
            return (false, null, $"Сталася помилка: {ex.Message}");
        }
    }
    static private async Task<string> GenerateInsuranceTextFromMistral()
    {
        var requestBody = new
        {
            model = "mistral-tiny",
            messages = new[]
            {
            new
            {
                role = "user",
                content = "Згенеруй типовий текст страхового полісу для автомобіля на англійській."
            }
        },
            max_tokens = 500
        };

        var requestJson = System.Text.Json.JsonSerializer.Serialize(requestBody);
        var requestMessage = new HttpRequestMessage(HttpMethod.Post, mistralUrl);
        requestMessage.Headers.Add("Authorization", $"Bearer {mistralApiKey}");
        requestMessage.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var response = await httpClient.SendAsync(requestMessage);

        var responseJson = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Сталася помилка: {response.StatusCode}");
            return null;
        }

        var jsonDoc = System.Text.Json.JsonDocument.Parse(responseJson);

        if (jsonDoc.RootElement.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var content))
            {
                return content.GetString();
            }
        }

        Console.WriteLine("Не знайдено нічого.");
        return null;
    }
    static private MemoryStream GeneratePdfFromText(string text)
    {
        var ms = new MemoryStream();
        var document = new iTextSharp.text.Document(iTextSharp.text.PageSize.A4, 40, 40, 40, 40);

        var writer = PdfWriter.GetInstance(document, ms);
        writer.CloseStream = false;

        document.Open();

        var baseFont = BaseFont.CreateFont(BaseFont.HELVETICA, BaseFont.CP1252, false);
        var font = new iTextSharp.text.Font(baseFont, 12, iTextSharp.text.Font.NORMAL);

        var paragraph = new Paragraph(text, font);
        document.Add(paragraph);

        document.Close();
        ms.Position = 0;

        return ms;
    }
}
