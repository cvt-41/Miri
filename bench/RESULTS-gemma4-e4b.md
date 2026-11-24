# Miri model benchmark

Run: 2026-11-23T15:59:19.351Z

| Model | Load s | Avg tok/s | Lang match | Tool calls | Vision | Avg chat s |
|---|---|---|---|---|---|---|
| gemma4:e4b | 12.6 | 7.0 | 4/4 | 5/5 | 3/3 (5s) | 15.7 |

## gemma4:e4b

### en-coffee — OK (replied en, 7.1 tok/s, 12.4s)
> How should I prepare my coffee?

Mrow. Depends on what you like, I guess. Are you in a hurry, or do you like to *savor* it?

If you want fast, a pour-over or a French press is decent. If you want strong, try a good espresso. Just make sure you use decent beans, or it's all pointless. Now stop bothering me. 🐾

### vi-coffee — OK (replied vi, 5.6 tok/s, 22.8s)
> Tôi nên pha cà phê phin như thế nào cho ngon?

*Hmph*. Pha cà phê phin ngon ư? Dễ thôi, nếu bạn không quá lười.

Đầu tiên, dùng cà phê xay vừa phải. Cho cà phê vào phin, nén nhẹ. Rót một chút nước sôi (khoảng 92-96°C) để "ủ" cà phê trong 30 giây. Sau đó, rót từ từ phần nước còn lại. Đợi nó nhỏ giọt hết.

Đừng vội. Sự kiên nhẫn là chìa khóa, đồ ngốc.

### en-kitten — OK (replied en, 7.9 tok/s, 12.4s)
> What type of kitten should I adopt? I live in a small apartment and work from home.

Hmph. Another kitten? Fine. For a small apartment and WFH, you need something low-energy. Look into British Shorthairs or Ragdolls. They are generally quite calm and don't require endless outdoor adventures.

Make sure they are litter-trained *before* they come home. Don't expect miracles. Now, if you'll excuse me, I need a nap. 😼

### vi-kitten — OK (replied vi, 7.5 tok/s, 15.3s)
> Tôi sống ở chung cư nhỏ và đi làm cả ngày, nên nhận nuôi loại mèo nào?

Hừm. Một cuộc sống bận rộn và không gian nhỏ, vậy à?

Nếu bạn không có nhiều thời gian, hãy chọn một giống mèo ít năng động. Mèo Ragdoll hoặc British Shorthair thường khá điềm tĩnh và thích nằm dài hơn là chạy nhảy liên tục.

Chúng cũng không quá đòi hỏi sự tương tác liên tục. Nhưng đừng mong chúng sẽ trở thành người bạn đồng hành hoạt bát nhất của bạn nhé. Giờ thì để tôi ngủ một chút. 🐾

### Tool calls
- **en-type** PASS: type_text({"id":4,"text":"see you at 7"}) (6.0s)
- **vi-type** PASS: type_text({"id":4,"text":"xin chào"}) (5.6s)
- **en-click** PASS: click({"id":3}) (4.9s)
- **vi-click** PASS: click({"id":6}) (4.9s)
- **no-tool** PASS: no tool (correct) — text: "The capital of France is Paris." (4.7s)

### Vision (4.9s)

The buttons in this dialog are: Save, Don't Save, and Cancel.

The button that saves the file is **Save**.
