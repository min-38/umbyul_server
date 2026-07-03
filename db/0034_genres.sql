-- NON-122: 유저 장르 태깅용 큐레이트 장르 사전. parent_id로 상위/서브장르 그룹(선택은 평면적).
create table if not exists public.genres (
    id         serial primary key,
    slug       text not null unique,
    name_ko    text not null,
    name_en    text not null,
    parent_id  int references public.genres (id),
    sort_order int not null default 0
);

-- 상위 장르
insert into public.genres (slug, name_ko, name_en, sort_order) values
    ('pop',        '팝',        'Pop',        10),
    ('kpop',       '케이팝',    'K-pop',      11),
    ('rock',       '록',        'Rock',       20),
    ('hiphop',     '힙합',      'Hip-hop',    30),
    ('rnb',        'R&B',       'R&B',        40),
    ('ballad',     '발라드',    'Ballad',     50),
    ('indie',      '인디',      'Indie',      60),
    ('electronic', '일렉트로닉','Electronic', 70),
    ('dance',      '댄스',      'Dance',      80),
    ('metal',      '메탈',      'Metal',      90),
    ('jazz',       '재즈',      'Jazz',       100),
    ('soul',       '소울',      'Soul',       110),
    ('funk',       '펑크',      'Funk',       120),
    ('blues',      '블루스',    'Blues',      130),
    ('folk',       '포크',      'Folk',       140),
    ('country',    '컨트리',    'Country',    150),
    ('classical',  '클래식',    'Classical',  160),
    ('latin',      '라틴',      'Latin',      170),
    ('trot',       '트로트',    'Trot',       180),
    ('ost',        'OST',       'OST',        190)
on conflict (slug) do nothing;

-- 서브 장르(parent_slug로 연결)
insert into public.genres (slug, name_ko, name_en, parent_id, sort_order)
select c.slug, c.name_ko, c.name_en, p.id, c.sort_order
from (values
    ('hardrock',   '하드록',        'Hard Rock',          'rock',       210),
    ('altrock',    '얼터너티브 록', 'Alternative Rock',   'rock',       211),
    ('progrock',   '프로그레시브 록','Progressive Rock',   'rock',       212),
    ('postrock',   '포스트록',      'Post-rock',          'rock',       213),
    ('punk',       '펑크 록',       'Punk',               'rock',       214),
    ('shoegaze',   '슈게이즈',      'Shoegaze',           'rock',       215),
    ('trap',       '트랩',          'Trap',               'hiphop',     220),
    ('boombap',    '붐뱁',          'Boom Bap',           'hiphop',     221),
    ('neosoul',    '네오소울',      'Neo-soul',           'rnb',        230),
    ('house',      '하우스',        'House',              'electronic', 240),
    ('techno',     '테크노',        'Techno',             'electronic', 241),
    ('edm',        'EDM',           'EDM',                'electronic', 242),
    ('synthpop',   '신스팝',        'Synth-pop',          'pop',        250),
    ('citypop',    '시티팝',        'City Pop',           'pop',        251),
    ('heavymetal', '헤비메탈',      'Heavy Metal',        'metal',      260)
) as c(slug, name_ko, name_en, parent_slug, sort_order)
join public.genres p on p.slug = c.parent_slug
on conflict (slug) do nothing;
