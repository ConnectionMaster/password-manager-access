using System.Linq;
using NUnit.Framework;

namespace LastPass.Test
{
    [TestFixture]
    class VaultTest
    {
        [Test]
        public void Create_returns_vault_with_correct_accounts()
        {
            var vault = Vault.Create(new Blob(TestData.Blob, 1));
            Assert.AreEqual(TestData.Accounts.Length, vault.Accounts.Length);
            Assert.AreEqual(TestData.Accounts.Select(i => i.Id), vault.Accounts.Select(i => i.Id));
            Assert.AreEqual(TestData.Accounts.Select(i => i.Url), vault.Accounts.Select(i => i.Url));
        }

        [Test]
        public void DecryptAllAccounts_decrypts_all_accounts()
        {
            var vault = Vault.Create(new Blob(TestData.Blob, 1));
            vault.DecryptAllAccounts(Account.Field.Name |
                                     Account.Field.Username |
                                     Account.Field.Password |
                                     Account.Field.Group,
                                     "p8utF7ZB8yD06SrtrD4hsdvEOiBU1Y19cr2dhG9DWZg=".Decode64());
            for (var i = 0; i < vault.Accounts.Length; ++i)
            {
                var account = vault.Accounts[i];
                var expectedAccount = TestData.Accounts[i];
                Assert.AreEqual(expectedAccount.Id, account.Id);
                Assert.AreEqual(expectedAccount.Name, account.Name.Decrypted);
                Assert.AreEqual(expectedAccount.Username, account.Username.Decrypted);
                Assert.AreEqual(expectedAccount.Password, account.Password.Decrypted);
                Assert.AreEqual(expectedAccount.Url, account.Url);
                Assert.AreEqual(expectedAccount.Group, account.Group.Decrypted);
            }
        }
    }
}
