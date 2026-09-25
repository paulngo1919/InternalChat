# Môi trường chạy dự án (Local Environments)

Dự án InternalChat cung cấp 2 cách để khởi chạy môi trường ở local, tùy thuộc vào mục đích của bạn là chạy thử để nghiệm thu hay để lập trình và gỡ lỗi (Local Debug).

---

## Cách 1: Chạy toàn bộ hệ thống (End-user / Nghiệm thu)
**Mục đích:** Khởi chạy nhanh dự án để dùng thử tính năng, chạy test E2E hoặc kiểm thử UI/UX mà không cần tải hay biên dịch mã nguồn. Mọi thành phần đều được chạy cô lập trong Docker.

**File cấu hình:** `docker-compose.yml`

**Các bước thực hiện:**
1. Khởi chạy toàn bộ nền tảng (Bao gồm Hạ tầng + API + Worker + Web + Nginx):
   ```bash
   cp deploy/.env.example deploy/.env
   docker compose -f deploy/docker-compose.yml up -d
   ```
2. Đợi 1-2 phút để Keycloak và Minio tự động thiết lập và đổ dữ liệu mẫu.
3. Truy cập:
   - Ứng dụng Web: `http://localhost:8080`
   - API Swagger: `http://localhost:8081/swagger`
4. Dùng các tài khoản seeding (như `an.nguyen`/`an.nguyen`) để đăng nhập và trải nghiệm.

*(Lưu ý: Nginx sẽ giữ cổng `8080` và `8081`. Các service API, Worker, Web chạy bên trong Docker ở chế độ `Release` nên **không thể đặt breakpoint debug**).*

---

## Cách 2: Chạy tách biệt Hạ tầng để Phát triển / Debug (Dành cho Developer)
**Mục đích:** Dành cho lập trình viên. Chỉ chạy các công cụ nền tảng trong Docker. Mã nguồn C# (.NET) và TypeScript (React) được chạy và build trực tiếp trên máy chủ vật lý (host) để IDE dễ dàng đính kèm (attach) Debugger.

**File cấu hình:** `docker-compose.infra.yml`

**Các bước thực hiện:**
1. Chắc chắn hệ thống của *Cách 1* đã được tắt đi để giải phóng các cổng mạng:
   ```bash
   docker compose -f deploy/docker-compose.yml down
   ```
2. Chạy **các dịch vụ hạ tầng** (Postgres, Redis, RabbitMQ, Keycloak, MinIO, ClamAV) và script Migration Database:
   ```bash
   docker compose -f deploy/docker-compose.infra.yml up -d --build
   ```
3. Mở dự án bằng Antigravity IDE (hoặc VS Code, Visual Studio, Rider).
4. Chuyển sang mục **Run and Debug**, chọn cấu hình **Full Stack (API + Web)** rồi nhấn F5.
   - IDE sẽ tự build project.
   - API chạy ở cổng `8081`.
   - Web App Vite chạy ở cổng `8080`.
   - Bạn có thể **đặt breakpoint trực tiếp** trong C# và các file `.tsx` để debug hệ thống.

### Xử lý sự cố thường gặp
- **Lỗi `Port already in use`:** Xảy ra do bạn chưa tắt môi trường ở *Cách 1*. Hãy thực hiện bước 1 của *Cách 2* để tắt container `nginx` đang giữ các cổng này.
- **Database báo thiếu bảng/schema:** Container `migrations` trong hạ tầng có thể chưa chạy xong. Hãy dùng lệnh `docker compose -f deploy/docker-compose.infra.yml logs migrations` để kiểm tra.
