-- Dados de exemplo (nomes e ids inventados).

INSERT INTO products (sku, name, kind, price_cents, coins, stock) VALUES
    ('COINS_500',   '500 moedas',              'coins',  990,  500, NULL),
    ('COINS_1200',  '1.200 moedas',            'coins', 1990, 1200, NULL),
    ('COINS_3000',  '3.000 moedas',            'coins', 4490, 3000, NULL),
    ('VIP_30D',     'VIP 30 dias',             'vip',   2490,    0, NULL),
    ('KIT_INICIAL', 'Kit inicial',             'kit',    790,    0, 50),
    ('KIT_LENDA',   'Kit lendário (edição 1)', 'kit',   9990,    0, 5);
-- @@
INSERT INTO coupons (code, percent_off, max_uses, expires_at) VALUES
    ('BEMVINDO10', 10, NULL, NULL),
    ('LANCAMENTO', 25, 100, '2030-12-31 23:59:59');
-- @@
INSERT INTO customers (steam_id, nickname, email) VALUES
    ('76561190000000001', 'Lobo',  'lobo@example.com'),
    ('76561190000000002', 'Corvo', NULL),
    ('76561190000000003', 'Raposa', 'raposa@example.com');
