-- Relatórios. Só pedidos pagos entram em venda.

CREATE VIEW v_wallet_balance AS
SELECT c.id AS customer_id,
       c.steam_id,
       c.nickname,
       COALESCE(SUM(w.amount), 0) AS balance
FROM customers c
LEFT JOIN wallet_entries w ON w.customer_id = c.id
GROUP BY c.id, c.steam_id, c.nickname;
-- @@
CREATE VIEW v_sales_daily AS
SELECT DATE(o.paid_at)        AS day,
       COUNT(*)               AS orders,
       SUM(o.total_cents)     AS revenue_cents,
       SUM(o.discount_cents)  AS discount_cents
FROM orders o
WHERE o.status = 'paid'
GROUP BY DATE(o.paid_at);
-- @@
CREATE VIEW v_top_products AS
SELECT p.sku,
       p.name,
       SUM(i.quantity)                       AS units,
       SUM(i.quantity * i.unit_price_cents)  AS gross_cents
FROM order_items i
JOIN orders o   ON o.id = i.order_id AND o.status = 'paid'
JOIN products p ON p.id = i.product_id
GROUP BY p.id, p.sku, p.name;
-- @@
CREATE VIEW v_customer_summary AS
SELECT c.id AS customer_id,
       c.steam_id,
       c.nickname,
       COUNT(o.id)                     AS paid_orders,
       COALESCE(SUM(o.total_cents), 0) AS spent_cents,
       MAX(o.paid_at)                  AS last_purchase
FROM customers c
LEFT JOIN orders o ON o.customer_id = c.id AND o.status = 'paid'
GROUP BY c.id, c.steam_id, c.nickname;
