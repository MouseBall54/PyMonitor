"""
디버깅 테스트용 임시 데이터 생성 스크립트.
numpy, opencv, pandas, matplotlib으로 변수에 테스트 데이터를 저장합니다.
"""

import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
import cv2

# ---------------------------------------------------------------------------
# NumPy: 수치 배열 및 통계 데이터
# ---------------------------------------------------------------------------
np_random_matrix = np.random.default_rng(42).random((5, 4))
np_int_array = np.arange(1, 21).reshape(4, 5)
np_stats = {
    "mean": float(np.mean(np_random_matrix)),
    "std": float(np.std(np_random_matrix)),
    "sum": float(np.sum(np_int_array)),
}

# ---------------------------------------------------------------------------
# Pandas: 테이블 형태 테스트 데이터
# ---------------------------------------------------------------------------
df_sales = pd.DataFrame(
    {
        "date": pd.date_range("2026-01-01", periods=10, freq="D"),
        "product": ["A", "B", "A", "C", "B", "A", "C", "B", "A", "C"],
        "quantity": [12, 8, 15, 6, 10, 9, 14, 7, 11, 13],
        "price": [1000, 1500, 1000, 2000, 1500, 1000, 2000, 1500, 1000, 2000],
    }
)
df_sales["revenue"] = df_sales["quantity"] * df_sales["price"]
df_summary = df_sales.groupby("product", as_index=False).agg(
    total_quantity=("quantity", "sum"),
    total_revenue=("revenue", "sum"),
)

# ---------------------------------------------------------------------------
# OpenCV: 테스트 이미지 생성
# ---------------------------------------------------------------------------
height, width = 240, 320
bgr_gradient = np.zeros((height, width, 3), dtype=np.uint8)
for y in range(height):
    for x in range(width):
        bgr_gradient[y, x] = [x % 256, y % 256, (x + y) % 256]

cv_image_color = bgr_gradient.copy()
cv2.rectangle(cv_image_color, (40, 40), (280, 200), (0, 255, 0), 2)
cv2.putText(
    cv_image_color,
    "Debug Test",
    (60, 120),
    cv2.FONT_HERSHEY_SIMPLEX,
    1.0,
    (255, 255, 255),
    2,
    cv2.LINE_AA,
)

cv_image_gray = cv2.cvtColor(cv_image_color, cv2.COLOR_BGR2GRAY)
cv_circle_mask = np.zeros((height, width), dtype=np.uint8)
cv2.circle(cv_circle_mask, (width // 2, height // 2), 60, 255, -1)

# ---------------------------------------------------------------------------
# Matplotlib: 그래프 Figure 객체 저장
# ---------------------------------------------------------------------------
fig_line, ax_line = plt.subplots(figsize=(6, 4))
ax_line.plot(df_sales["date"], df_sales["revenue"], marker="o", label="Revenue")
ax_line.set_title("Daily Revenue")
ax_line.set_xlabel("Date")
ax_line.set_ylabel("Revenue")
ax_line.legend()
ax_line.grid(True, alpha=0.3)
fig_line.tight_layout()

fig_bar, ax_bar = plt.subplots(figsize=(6, 4))
ax_bar.bar(df_summary["product"], df_summary["total_revenue"], color=["#4C72B0", "#55A868", "#C44E52"])
ax_bar.set_title("Revenue by Product")
ax_bar.set_xlabel("Product")
ax_bar.set_ylabel("Total Revenue")
fig_bar.tight_layout()

fig_scatter, ax_scatter = plt.subplots(figsize=(6, 4))
ax_scatter.scatter(df_sales["quantity"], df_sales["price"], c=df_sales["revenue"], cmap="viridis")
ax_scatter.set_title("Quantity vs Price")
ax_scatter.set_xlabel("Quantity")
ax_scatter.set_ylabel("Price")
fig_scatter.tight_layout()

# 디버거에서 바로 확인할 수 있는 요약 변수
debug_summary = {
    "numpy_shapes": {
        "np_random_matrix": np_random_matrix.shape,
        "np_int_array": np_int_array.shape,
    },
    "pandas_rows": len(df_sales),
    "opencv_image_shape": cv_image_color.shape,
    "matplotlib_figures": ["fig_line", "fig_bar", "fig_scatter"],
}


if __name__ == "__main__":
    print("=== Debug Test Data Summary ===")
    print(f"NumPy matrix shape: {np_random_matrix.shape}")
    print(f"NumPy stats: {np_stats}")
    print()
    print("Pandas sales data:")
    print(df_sales.head())
    print()
    print("Pandas summary:")
    print(df_summary)
    print()
    print(f"OpenCV color image shape: {cv_image_color.shape}, dtype: {cv_image_color.dtype}")
    print(f"OpenCV gray image shape: {cv_image_gray.shape}")
    print()
    print(f"Matplotlib figures ready: {debug_summary['matplotlib_figures']}")
    print()
    print("브레이크포인트를 걸고 변수를 확인하세요.")
