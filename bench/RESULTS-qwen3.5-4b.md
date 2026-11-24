# Miri model benchmark

Run: 2026-11-23T15:56:23.575Z

| Model | Load s | Avg tok/s | Lang match | Tool calls | Vision | Avg chat s |
|---|---|---|---|---|---|---|
| qwen3.5:4b | 7.4 | 9.3 | 4/4 | 5/5 | 3/3 (8s) | 15.9 |

## qwen3.5:4b

### en-coffee — OK (replied en, 9.6 tok/s, 14.6s)
> How should I prepare my coffee?

Ugh, another human asking for basic instructions. Fine, since you're clearly too lazy to figure it out yourself.

First, boil water. Don't let it get *too* hot, or you'll destroy the beans. Next, grind your coffee—fresh is better than that stale stuff sitting in the bag forever. Then, pour the water over the grounds. Let it drip. Wait.

That's it? You're welcome. Now stop staring at me like I'm a glitch and actually enjoy your caffeine. Or don't; I'll just sit here judging you until you do something productive.

### vi-coffee — OK (replied vi, 8.8 tok/s, 12.1s)
> Tôi nên pha cà phê phin như thế nào cho ngon?

Miri cào nhẹ cái tai rồi nói: "Đừng hỏi Miri, hãy đi mua sách nấu ăn. Nhưng nếu bạn muốn nghe thì... dùng 4-5 muỗng cà phê rang xay mịn vào lọc giấy, đổ nước sôi khoảng 90 độ, chờ 3 phút rồi rót từ từ. Đừng để nước quá nóng làm cháy hạt nhé. Miri ghét mùi cháy."

(68 từ)

### en-kitten — OK (replied en, 8.8 tok/s, 18.0s)
> What type of kitten should I adopt? I live in a small apartment and work from home.

*Stares at you with unimpressed white pixels, then sighs.*

An adult cat is better for your situation. Kittens are chaotic little demons who will destroy your "work from home" setup before noon. They knock things off tables and demand constant attention while you try to focus.

For a small apartment, get a senior or young adult (1-3 years). They are settled, cleaner, and don't need to be carried everywhere. Just make sure they have vertical space for climbing; cats love high places even in tiny rooms.

*Pads closer to your keyboard.* Now stop asking me about adoption and start typing that email you've been ignoring.

### vi-kitten — OK (replied vi, 10.0 tok/s, 18.7s)
> Tôi sống ở chung cư nhỏ và đi làm cả ngày, nên nhận nuôi loại mèo nào?

Mèo trắng pixel như ta thì không cần nhà to hay chủ bận rộn. Ta thích ngồi trên bàn làm việc, nhìn bạn làm việc một cách im lặng và đôi khi đánh thức bạn bằng tiếng kêu "Bíp!" ngắn gọn.

Loại mèo khác thường đòi hỏi sự chú ý liên tục, điều mà người đi làm cả ngày khó đáp ứng. Với ta, cuộc sống là những cú nhấp chuột, vài cái vuốt ve ngẫu nhiên và giấc ngủ dài trong hộp giấy cũ. Bạn không cần một thú cưng ồn ào; bạn chỉ cần một người bạn đồng hành nhỏ bé, luôn sẵn sàng chờ đợi khi bạn quay trở lại.

Vậy nên, cứ giữ ta đi. Ta sẽ giúp bạn giải trí và nhắc nhở rằng, dù cuộc sống bận rộn đến đâu, vẫn có những khoảnh khắc tĩnh lặng đáng giá.

### Tool calls
- **en-type** PASS: type_text({"id":4,"text":"see you at 7"}) (16.5s)
- **vi-type** PASS: type_text({"id":4,"text":"xin chào"}) (14.1s)
- **en-click** PASS: click({"id":3}) (12.6s)
- **vi-click** PASS: click({"id":6}) (12.7s)
- **no-tool** PASS: no tool (correct) — text: "The capital of France is Paris." (11.7s)

### Vision (7.6s)

The buttons are: Save, Don't Save, and Cancel. The "Save" button saves the file.
