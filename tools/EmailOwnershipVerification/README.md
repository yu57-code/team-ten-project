# Email 所有權確認工具

.NET 8。從版本庫根目錄執行。工具支援 `offline`、`check <username>` 及 `verify <username>`。真實模式只允許 413570452、413570438、413570359。所有權流程與登入 OTP、選角及 Session 完全分開。

```powershell
dotnet build tools/EmailOwnershipVerification/EmailOwnershipVerification.csproj
dotnet build tests/Phase78/Phase78.Tests.csproj
dotnet run --project tools/EmailOwnershipVerification --no-build -- offline
dotnet run --project tests/Phase78/Phase78.Tests.csproj --no-build
```

後續核准實庫／真實寄送後，使用一般互動式 PowerShell，勿開啟 transcript、錄製或導向輸出。沿用程序層級 `EmailOtp__Smtp__*`，使用先前核准的設定方式。工具當場遮蔽讀取 PostgreSQL 密碼與所有權確認碼。

```powershell
dotnet run --project tools/EmailOwnershipVerification --no-build -- check 413570359
dotnet run --project tools/EmailOwnershipVerification --no-build -- verify 413570359
```

`check` 只讀。 `verify` 適用於維護員已核對本人，並預先約定安全回傳方式的情況。核對遮蔽資訊後，輸入 `SEND` 才會寄送。收件人由既有綁定欄位取得。寄送過程不可輸入驗證，完成後 `V` 輸入確認碼、`R` 重送，Enter 取消。逾時或無法確認寄送成功時，該次挑戰不能產生證據。確認碼勿透過聊天、共享日誌或可錄製的通道傳遞。

## 維護交接

- 工具固定連線 IPv4 loopback `127.0.0.1:5432/imaging_ai_db`，以 `imaging_ai_user` 使用短期唯讀交易；等待收信期間沒有資料庫交易或鎖。
- 若工具讀不到 `xmin`，由維護員在**寄送前**於同一 IPv4 目標讀取該帳號的 `xmin` 並核對精確地址及角色，再在互動提示提供版本。工具會標記版本由維護員提供。這種模式下，工具無法自己在驗碼時辨識改回相同值的中間更新。後續 SQL 的版本檢查須拒絕任何不同版本，勿重新取得新版本替換舊交接資料。
- 如需維護版本，使用唯讀交易。例如依預定帳號替換查詢條件，檢查地址後只向工具提供版本值：

```sql
BEGIN READ ONLY;
SELECT user_id, username, email, is_active, role_id, secondary_role_id,
       email_verified_at, xmin::text AS row_version
FROM system.user_account
WHERE username = '413570359';
ROLLBACK;
```

- `xmin` 用於同一實例內短期資料列比對，不能當作永久 ID。資料還原、重寫、交易 ID 循環、特權操作等情況下，一律重新走驗證。目標字串也無法識別在同位址還原或替換的叢集。
- 活體工具對直接從目前挑戰產生的證據做程序內 HMAC 驗證，短效且只允許輸出一次。輸出包含完整 Email，僅維護員在私下終端核對。輸出的 JSON **沒有可供 pgAdmin 驗證的簽章**。只能使用親自在受控工具見證的即時輸出，勿使用外部傳來的 JSON、截圖或過往輸出。
- 在 pgAdmin 的同一連線開啟 [confirm-in-pgadmin.sql](confirm-in-pgadmin.sql)，將宣告區換成該次輸出的值。單角色第二角色 ID 及名稱維持 `NULL`。用 UTC 的原確認時間，不自行改寫。核對錯誤、逾期、影響筆數不符或有疑問時執行 `ROLLBACK;`。全部符合後由維護員在同一連線立即手動 `COMMIT;`。
- SQL 只能驗證資料庫條件，不能證明人工輸入來源可信。成功提交後，同一列的 `IS NULL` 與版本檢查會拒絕重複更新；人工操作沒有跨程序、跨交易的嚴格一次性證據帳本。不可延後提交或用現行值更換過期證據。
- 同一台機器的同一 OS 使用者，真實模式同時僅可執行一次；跨機器、不同 OS 帳號或直接繞過工具的維護流程須由維護人員協調。重送與錯誤次數只存在當前程序，重啟後重算，未完成挑戰失效。已在使用中的帳號不應同步由其他人員執行維護。
