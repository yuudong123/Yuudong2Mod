# 파동 미니건과 쐐기탄 그림을 그린다. 4배 크기로 그린 뒤 줄여서 림월드 무기 그림처럼 굵은 외곽선과 단순한 명암을 낸다.
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 4
OUT = 128
OUTLINE = (12, 12, 14, 255)


def c(v):
    return int(round(v * S))


def shade(rgb, k):
    return tuple(max(0, min(255, int(x * k))) for x in rgb) + (255,)


class Canvas:
    def __init__(self, size):
        self.size = size
        self.img = Image.new("RGBA", (c(size), c(size)), (0, 0, 0, 0))

    def part(self, box, color, radius=1.0, outline=1.6, light=1.25, dark=0.7, band=0.28):
        # 둥근 사각형 부품: 채우고, 위쪽 밝은 띠와 아래쪽 어두운 띠를 넣고, 외곽선을 두른다.
        x0, y0, x1, y1 = [c(v) for v in box]
        r = c(radius)
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        d.rounded_rectangle((x0, y0, x1, y1), r, fill=shade(color, 1.0))
        mask = Image.new("L", self.img.size, 0)
        ImageDraw.Draw(mask).rounded_rectangle((x0, y0, x1, y1), r, fill=255)
        h = y1 - y0
        hl = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        hd = ImageDraw.Draw(hl)
        hd.rectangle((x0, y0, x1, y0 + int(h * band)), fill=shade(color, light))
        hd.rectangle((x0, y1 - int(h * band), x1, y1), fill=shade(color, dark))
        hl.putalpha(ImageChops.multiply(hl.getchannel("A"), mask))
        layer.alpha_composite(hl)
        ImageDraw.Draw(layer).rounded_rectangle((x0, y0, x1, y1), r, outline=OUTLINE, width=c(outline))
        self.img.alpha_composite(layer)

    def poly(self, pts, color, outline=1.6):
        layer = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        d = ImageDraw.Draw(layer)
        p = [(c(x), c(y)) for x, y in pts]
        d.polygon(p, fill=shade(color, 1.0))
        d.line(p + [p[0]], fill=OUTLINE, width=c(outline), joint="curve")
        self.img.alpha_composite(layer)

    def rect(self, box, rgba):
        ImageDraw.Draw(self.img).rectangle([c(v) for v in box], fill=rgba)

    def ellipse(self, box, rgba, outline=None, width=1.0):
        ImageDraw.Draw(self.img).ellipse([c(v) for v in box], fill=rgba, outline=outline, width=c(width) if outline else 0)

    def glow(self, box, rgba, blur=2.5):
        # 파동 에너지가 새어 나오는 빛
        g = Image.new("RGBA", self.img.size, (0, 0, 0, 0))
        ImageDraw.Draw(g).rounded_rectangle([c(v) for v in box], c(1), fill=rgba)
        g = g.filter(ImageFilter.GaussianBlur(c(blur)))
        self.img.alpha_composite(g)

    def finish(self, path, out):
        # 바깥 실루엣에 외곽선을 한 겹 더 둘러 게임 안에서 또렷하게 보이게 한다.
        alpha = self.img.getchannel("A").point(lambda a: 255 if a > 40 else 0)
        grown = alpha.filter(ImageFilter.MaxFilter(c(0.75) * 2 + 1))
        base = Image.new("RGBA", self.img.size, OUTLINE)
        base.putalpha(grown)
        base.alpha_composite(self.img)
        base.resize((out, out), Image.LANCZOS).save(path)


GUN = (78, 80, 86)
DARK = (46, 47, 52)
STEEL = (120, 124, 130)
CYAN = (90, 230, 245)


def gun(path):
    k = Canvas(OUT)
    # 손잡이와 뒤쪽 모터 뭉치
    k.poly([(30, 70), (37, 70), (35, 84), (27, 84)], DARK)
    k.part((14, 52, 34, 70), DARK, radius=3)
    k.part((17, 56, 30, 61), STEEL, radius=1, outline=1.0)
    # 아래쪽 에너지 전지
    k.part((40, 66, 62, 80), DARK, radius=2)
    k.part((44, 70, 58, 75), (30, 70, 78), radius=1, outline=1.0)
    k.glow((44, 70, 58, 75), (90, 230, 245, 200), blur=1.6)
    k.rect((45, 71, 57, 73.5), (170, 250, 255, 255))
    # 몸체
    k.part((30, 50, 78, 68), GUN, radius=3)
    # 위쪽 손잡이와 특수망원렌즈
    k.part((40, 42, 66, 47), DARK, radius=1.5)
    k.part((38, 47, 42, 51), DARK, radius=0.8, outline=1.2)
    k.part((62, 47, 66, 51), DARK, radius=0.8, outline=1.2)
    k.part((44, 36, 62, 42), GUN, radius=2)
    k.part((61, 36.5, 65, 41.5), STEEL, radius=1, outline=1.2)
    k.ellipse((62.2, 37.6, 64.2, 40.4), (255, 90, 80, 255))
    # 몸체 옆 파동 코일 창과 표시등
    k.part((46, 55, 72, 61), (24, 40, 46), radius=1, outline=1.0)
    for i in range(5):
        x = 48 + i * 4.8
        k.rect((x, 56, x + 2.6, 60), (120, 240, 250, 255))
    k.glow((46, 55, 72, 61), (90, 230, 245, 150), blur=2.0)
    k.ellipse((33, 54, 36, 57), (230, 60, 50, 255))
    k.rect((33, 63, 44, 64.2), (150, 152, 158, 255))
    # 총열 고정 고리 사이의 회전 총열 다발(보이는 세 줄)
    k.part((76, 49, 84, 69), DARK, radius=2)
    for y in (50.5, 56.5, 62.5):
        k.part((82, y, 118, y + 5), STEEL, radius=2.4, outline=1.3, light=1.35, dark=0.65, band=0.33)
    # 총열 중간·끝 고리
    k.part((96, 48.5, 101, 69.5), DARK, radius=1.5)
    k.part((112, 48, 119, 70), GUN, radius=2)
    # 총구 안쪽 파동 빛
    for y in (50.5, 56.5, 62.5):
        k.glow((116, y + 1, 121, y + 4), (90, 230, 245, 220), blur=1.4)
    # 고리의 파동 띠
    k.rect((97, 52, 100, 66), (90, 230, 245, 255))
    k.glow((96, 51, 101, 67), (90, 230, 245, 160), blur=1.8)
    k.finish(path, OUT)


def bullet(path):
    # 쐐기탄: 위쪽이 진행 방향이다.
    k = Canvas(64)
    k.glow((26, 10, 38, 54), (60, 210, 240, 170), blur=4)
    layer = Image.new("RGBA", k.img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.polygon([(c(32), c(8)), (c(38), c(30)), (c(35), c(54)), (c(29), c(54)), (c(26), c(30))], fill=(110, 235, 250, 255))
    d.polygon([(c(32), c(12)), (c(35), c(30)), (c(33), c(48)), (c(31), c(48)), (c(29), c(30))], fill=(230, 255, 255, 255))
    k.img.alpha_composite(layer)
    k.img.resize((64, 64), Image.LANCZOS).save(path)


if __name__ == "__main__":
    out = sys.argv[1]
    gun(out + "/PulseMinigun.png")
    bullet(out + "/PulseMinigunBullet.png")
