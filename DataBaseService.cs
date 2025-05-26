using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Npgsql;

namespace InsuranceBot
{
    internal class DataBaseService
    {
        private readonly string _connectionString;

        public DataBaseService(string connectionString)
        {
            _connectionString = connectionString;
        }
        public async Task SaveDocumentToDataBase(long userId, string documentType, byte[] image)
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var cmd = new NpgsqlCommand("INSERT INTO documents (user_id, document_type, image) VALUES (@userId, @docType, @image)",conn);
            cmd.Parameters.AddWithValue("userId", userId);
            cmd.Parameters.AddWithValue("docType", documentType);
            cmd.Parameters.AddWithValue("image", image);

            await cmd.ExecuteNonQueryAsync();
        }
    }
}
