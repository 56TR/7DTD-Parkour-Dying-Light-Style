# Mechanical checks of tools/i18n/strings.json (the meaning is checked separately by back-translation).
#   python -I tools/i18n/check.py <mod root>
# Exit code 1 when any check fails.
import json
import os
import re
import sys
import unicodedata

sys.stdout.reconfigure(encoding="utf-8")

root = sys.argv[1]
data = json.load(open(os.path.join(root, 'tools', 'i18n', 'strings.json'), encoding='utf-8'))
gpath = os.path.join(root, 'tools', 'i18n', 'vanilla_glossary.json')
if not os.path.exists(gpath):
    sys.exit('vanilla_glossary.json missing: python -I tools/i18n/extract_glossary.py <mod root> "<game dir>"')
gloss = json.load(open(gpath, encoding='utf-8'))
langs = data['languages']
entries = data['entries']
by_key = {e['key']: e for e in entries}
errors, warnings = [], []


def err(key, lang, msg):
    errors.append(f'{key} [{lang}]: {msg}')


def warn(key, lang, msg):
    warnings.append(f'{key} [{lang}]: {msg}')


# 1. settings keys match the C# code
cs = open(os.path.join(root, 'src', 'Game', 'PkSettings.cs'), encoding='utf-8').read()
code_keys = re.findall(r'new Def \{ Key = "(\w+)"', cs)
label_keys = [e['key'][5:] for e in entries if e['kind'] == 'label']
if code_keys != label_keys:
    errors.append(f'option rows {label_keys} do not match PkSettings.Defs {code_keys}')
for k in code_keys:
    if 'xuiPk' + k + 'Tooltip' not in by_key:
        errors.append(f'missing tooltip for {k}')
perk_code = set(re.findall(r'"pkPerk(\w+)"', open(os.path.join(root, 'src', 'Game', 'PerkIntegration.cs'), encoding='utf-8').read()))
perk_code |= {'Mantle', 'Slide', 'LowSlide', 'LandingRoll', 'Vault', 'Hang', 'SlideJump', 'WallClimb', 'WallRun', 'WallJump',
              'Tackle', 'Dropkick', 'Stomp', 'VaultEnemy', 'Efficient', 'HangFree', 'AutoRoll'}
for k in perk_code:
    if 'pkPerk' + k not in by_key:
        errors.append(f'code uses pkPerk{k}, not in strings.json')

SCRIPT = {
    'japanese': lambda c: '぀' <= c <= 'ヿ',          # kana
    'koreana': lambda c: '가' <= c <= '힯',           # hangul
    'russian': lambda c: 'Ѐ' <= c <= 'ӿ',           # cyrillic
    'schinese': lambda c: '一' <= c <= '鿿',
    'tchinese': lambda c: '一' <= c <= '鿿',
}
CJK = {'japanese', 'koreana', 'schinese', 'tchinese'}
# simplified-only / traditional-only characters that must not appear in the other variant
SIMPLIFIED_ONLY = set('铲动击级尸冲跃体墙边缘悬挂围块绕获开关显现时样头启连奖设备为这个过还')
TRADITIONAL_ONLY = set('鏟動擊級屍衝躍體牆邊緣懸掛圍塊繞獲開關顯現時樣頭啟連獎設備為這個過還')


def width(s):
    return sum(2 if unicodedata.east_asian_width(c) in 'WF' else 1 for c in s)


def stem(word, lang):
    w = word.lower().strip('«»"\'.: ')
    if lang in CJK:
        return w
    return w[:max(3 if lang in ('russian', 'polish') else 4, len(w) - 3)]


def term(key, lang):
    v = gloss[key][lang]
    v = re.sub(r'^[^:：]*[:：]\s*', '', v) if key == 'attAgilityName' else v   # "Attribut: Beweglichkeit" -> name
    return v


for e in entries:
    key, en = e['key'], e['english']
    ph = sorted(re.findall(r'\{\d+\}', en))
    nums = sorted(re.findall(r'\d+', re.sub(r'\{\d+\}', '', en)))
    for lang in langs:
        t = e.get(lang)
        if not t:
            err(key, lang, 'missing')
            continue
        if sorted(re.findall(r'\{\d+\}', t)) != ph:
            err(key, lang, f'placeholders {re.findall(r"{\\d+}", t)} != {ph}')
        if t.count('[') != t.count(']'):
            err(key, lang, 'unbalanced [ ]')
        tn = sorted(re.findall(r'\d+', re.sub(r'\{\d+\}|1인칭', '', t)))
        if re.search(r'\bone\b', en) and '1' in tn and tn.count('1') > nums.count('1'):
            tn.remove('1')
        if tn != nums:
            # the vanilla Russian style writes small numbers as words ("пять метров")
            if not (lang == 'russian' and key == 'pkPerkSafeFall5') and not (lang == 'russian' and key == 'xuiPkNoFallImmunityTooltip'):
                err(key, lang, f'numbers {tn} != {nums}')
        if lang == 'english':
            continue
        if lang in SCRIPT and not e.get('brand') and not any(SCRIPT[lang](c) for c in t):
            err(key, lang, 'no characters of this script')
        if lang == 'schinese' and any(c in TRADITIONAL_ONLY for c in t):
            err(key, lang, 'traditional characters in simplified Chinese: ' + ''.join(c for c in t if c in TRADITIONAL_ONLY))
        if lang == 'tchinese' and any(c in SIMPLIFIED_ONLY for c in t):
            err(key, lang, 'simplified characters in traditional Chinese: ' + ''.join(c for c in t if c in SIMPLIFIED_ONLY))
        if t == en and not e.get('brand') and e['kind'] != 'tab' and t not in ('Dropkick',):
            warn(key, lang, 'same as English')
        if e['kind'] == 'label' and width(t) > 30:
            err(key, lang, f'label too long for the options row ({width(t)} > 30 columns)')
        if e['kind'] == 'tab' and width(t) > 14:
            err(key, lang, 'tab caption too long')
        if t != t.strip() or '  ' in t:
            err(key, lang, 'extra spaces')
        # official game terms
        low = t.lower()
        checks = []
        if re.search(r'(?<![.!?] )(?<!^)\bCrouch\b', en):
            checks.append(('inpActPlayerCrouchName', 'Crouch'))
        if 'stamina' in en.lower():
            checks.append(('xuiBuffStatStamina', 'stamina'))
        if 'zombie' in en.lower():
            checks.append(('xuiZombies', 'zombie'))
        if 'fall damage' in en.lower():
            checks.append(('statFallDamage', 'fall damage'))
        if "Specter's Grace" in en:
            checks.append(('perkAgilityMasteryRank4Desc', "Specter's Grace"))
        if 'Agility Mastery' in en:
            checks.append(('perkAgilityMasteryName', 'Agility Mastery'))
        elif 'Agility' in en:
            checks.append(('attAgilityName', 'Agility'))
        if 'Parkour perk' in en or 'Parkour level' in en:
            checks.append(('perkParkourName', 'Parkour'))
        for gk, what in checks:
            official = term(gk, lang)
            words = [w for w in re.split(r'[\s/]+', official) if w]
            ok = all(stem(w, lang) in low for w in words) if lang not in CJK else official.lower() in low
            if gk == 'xuiZombies':
                # plural in the glossary; accept the singular stem too
                ok = ok or stem(official, lang)[:4] in low or official.rstrip('s').lower()[:4] in low
            if gk == 'xuiBuffStatStamina' and lang in ('schinese', 'japanese', 'koreana', 'tchinese'):
                ok = ok or official[:2] in t
            if gk == 'statFallDamage' and lang in CJK:
                ok = ok or official[-2:] in t
            if not ok:
                err(key, lang, f'"{what}" should use the game\'s term "{official}"')
        # tiers
        for n in re.findall(r'level (\d)', en):
            if n not in t:
                err(key, lang, f'level {n} missing')

# move names: the perk line starts with the option label; tooltips naming another option use its label
for e in entries:
    if not e['key'].startswith('pkPerk'):
        continue
    label = by_key.get('xuiPk' + e['key'][6:])
    if not label:
        continue
    for lang in langs:
        t, name = e.get(lang, ''), label.get(lang, '')
        if not re.match(re.escape(name) + r'\s?[:：]', t):
            err(e['key'], lang, f'should start with the option name "{name}:"')
REFS = {'xuiPkAutoRollTooltip': ['xuiPkLandingRoll'], 'xuiPkBodyAnimsTooltip': ['xuiPkFpBody']}
for k, refs in REFS.items():
    for r in refs:
        for lang in langs:
            name = by_key[r][lang]
            t = by_key[k][lang]
            if stem(name.split()[0], lang)[:5].lower() not in t.lower():
                err(k, lang, f'should name the option "{name}"')

for w in warnings:
    print('WARN', w)
for x in errors:
    print('FAIL', x)
print(f'{len(entries)} texts x {len(langs)} languages: {len(errors)} errors, {len(warnings)} warnings')
sys.exit(1 if errors else 0)
